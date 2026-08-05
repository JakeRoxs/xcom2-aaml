param(
    [Parameter(Mandatory = $true)][ValidateSet('windows-game', 'linux-proton', 'steam-mutation')][string]$Scenario,
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ExpectedArchiveSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedRepository,
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$RunnerConfigPath,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TreeHashes([string]$Path) {
    $hashes = [ordered]@{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $hashes['.'] = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    } elseif (Test-Path -LiteralPath $Path -PathType Container) {
        Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
            $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return $hashes
}

function Get-State($Items) {
    $state = [ordered]@{}
    foreach ($item in $Items) {
        $state[$item.name] = [ordered]@{ exists = Test-Path -LiteralPath $item.path; treeSha256 = Get-TreeHashes $item.path }
    }
    return $state
}

function Get-JsonFingerprint($Value) { return ($Value | ConvertTo-Json -Depth 20 -Compress) }

function Copy-EvidenceLogs($Paths, [string]$Destination) {
    $result = @()
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $index = 0
    foreach ($path in $Paths) {
        foreach ($file in @(Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue)) {
            $index++
            $leaf = "{0:D4}-{1}" -f $index, $file.Name
            $target = Join-Path $Destination $leaf
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $result += [ordered]@{ source = $file.FullName; evidencePath = "logs/$leaf"; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    }
    return $result
}

if ([string]::IsNullOrWhiteSpace($RunnerConfigPath) -or -not (Test-Path -LiteralPath $RunnerConfigPath -PathType Leaf)) { throw 'AAML_QUALIFICATION_CONFIG must identify a runner-local configuration file.' }
if (Test-Path -LiteralPath $WorkDirectory) { Remove-Item -LiteralPath $WorkDirectory -Recurse -Force }
if (Test-Path -LiteralPath $EvidenceDirectory) { Remove-Item -LiteralPath $EvidenceDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $WorkDirectory, $EvidenceDirectory -Force | Out-Null

$started = [DateTimeOffset]::UtcNow
$config = Get-Content -LiteralPath $RunnerConfigPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 1 -or $config.scenario -cne $Scenario) { throw 'Runner configuration schema/scenario does not match the selected protected job.' }
if ($config.timeoutSeconds -lt 30 -or $config.timeoutSeconds -gt 2400) { throw 'Runner timeout must be between 30 and 2400 seconds.' }
$stateItems = @($config.statePaths)
if ($stateItems.Count -eq 0) { throw 'Runner configuration must declare statePaths.' }
$names = @($stateItems | ForEach-Object name)
if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Runner state path names must be unique.' }
if ($Scenario -eq 'steam-mutation' -and @($stateItems | Where-Object policy -eq 'rollback').Count -eq 0) { throw 'Steam mutation requires at least one rollback state path.' }
foreach ($item in $stateItems) {
    if (-not [IO.Path]::IsPathRooted($item.path) -or $item.policy -notin @('preserve', 'rollback')) { throw "State path '$($item.name)' is invalid." }
}

$archive = & (Join-Path $PSScriptRoot 'assert-exact-artifact.ps1') -ArchivePath $ArchivePath -ExpectedArchiveSha256 $ExpectedArchiveSha256 -ExpectedRepository $ExpectedRepository -ExpectedCommit $ExpectedCommit -ExpectedVersion $ExpectedVersion -ExtractionDirectory (Join-Path $WorkDirectory 'extracted')
$expectedRid = if ($Scenario -eq 'linux-proton') { 'linux-x64' } else { 'win-x64' }
if ($archive.rid -cne $expectedRid) { throw "Scenario $Scenario requires $expectedRid, not $($archive.rid)." }
$manifest = Join-Path (Split-Path -Parent $PSScriptRoot) "package-manifests/aaml-$expectedRid.json"
& (Join-Path (Split-Path -Parent $PSScriptRoot) 'validate-aaml-artifact.ps1') -ArtifactDirectory $archive.artifactDirectory -ManifestPath $manifest -VerifyChecksums
if (-not $?) { throw 'Extracted package policy validation failed.' }

$artifactBefore = Get-TreeHashes $archive.artifactDirectory
$stateBefore = Get-State $stateItems
$backupRoot = Join-Path $WorkDirectory 'rollback'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($item in @($stateItems | Where-Object policy -eq 'rollback')) {
    if (Test-Path -LiteralPath $item.path) { Copy-Item -LiteralPath $item.path -Destination (Join-Path $backupRoot $item.name) -Recurse -Force }
}

$processesBefore = Get-Process | Sort-Object Id | Select-Object Id, ProcessName, Path
$processesBefore | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'processes-before.json') -Encoding utf8
$stdout = Join-Path $EvidenceDirectory 'scenario.stdout.log'
$stderr = Join-Path $EvidenceDirectory 'scenario.stderr.log'
$configuredArguments = if ($null -ne $config.PSObject.Properties['arguments']) { @($config.arguments) } else { @() }
$arguments = @($configuredArguments | ForEach-Object { $_.Replace('{artifact}', $archive.artifactDirectory).Replace('{evidence}', $EvidenceDirectory) })
$command = $config.command.Replace('{artifact}', $archive.artifactDirectory).Replace('{evidence}', $EvidenceDirectory)
$process = $null
$exitCode = $null
$timedOut = $false
$failure = $null
$stateAfterScenario = $null
$stateAfterRestoration = $null
$restorationMismatches = @()
$logs = @()
try {
    $process = Start-Process -FilePath $command -ArgumentList $arguments -WorkingDirectory $archive.artifactDirectory -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    if (-not $process.WaitForExit([int]$config.timeoutSeconds * 1000)) {
        $timedOut = $true
        $process.Kill($true)
        $process.WaitForExit()
        throw "Scenario exceeded bounded timeout of $($config.timeoutSeconds) seconds."
    }
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) { throw "Scenario command exited with code $exitCode." }
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $stateAfterScenario = Get-State $stateItems
    $logs = @(Copy-EvidenceLogs @($config.logPaths) (Join-Path $EvidenceDirectory 'logs'))
    foreach ($item in @($stateItems | Where-Object policy -eq 'rollback')) {
        if (Test-Path -LiteralPath $item.path) { Remove-Item -LiteralPath $item.path -Recurse -Force }
        $backup = Join-Path $backupRoot $item.name
        if (Test-Path -LiteralPath $backup) { Copy-Item -LiteralPath $backup -Destination $item.path -Recurse -Force }
    }
    $stateAfterRestoration = Get-State $stateItems
    foreach ($item in $stateItems) {
        $beforeFingerprint = Get-JsonFingerprint $stateBefore[$item.name]
        $afterFingerprint = Get-JsonFingerprint $stateAfterRestoration[$item.name]
        if ($beforeFingerprint -cne $afterFingerprint) { $restorationMismatches += $item.name }
    }
}

$processesAfter = Get-Process | Sort-Object Id | Select-Object Id, ProcessName, Path
$processesAfter | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'processes-after.json') -Encoding utf8
$artifactAfter = Get-TreeHashes $archive.artifactDirectory
if ((Get-JsonFingerprint $artifactBefore) -cne (Get-JsonFingerprint $artifactAfter)) { $restorationMismatches += 'extracted-artifact' }
$restorationComplete = $restorationMismatches.Count -eq 0
if (-not $restorationComplete -and $null -eq $failure) { $failure = "Restoration/source preservation mismatch: $($restorationMismatches -join ', ')" }

$evidence = [ordered]@{
    schemaVersion = 1
    scenario = $Scenario
    result = if ($null -eq $failure) { 'passed' } else { 'failed' }
    failure = $failure
    startedAtUtc = $started.ToString('o')
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    artifact = [ordered]@{ archiveFile = Split-Path -Leaf $ArchivePath; archiveSha256 = $archive.archiveSha256; rid = $archive.rid; repository = $archive.repository; commit = $archive.commit; version = $archive.version; treeBeforeSha256 = $artifactBefore; treeAfterSha256 = $artifactAfter }
    runner = [ordered]@{ os = [Environment]::OSVersion.ToString(); machine = [Environment]::MachineName; configSha256 = (Get-FileHash -LiteralPath $RunnerConfigPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    command = [ordered]@{ file = $command; arguments = $arguments; timeoutSeconds = [int]$config.timeoutSeconds; exitCode = $exitCode; timedOut = $timedOut }
    state = [ordered]@{ before = $stateBefore; afterScenario = $stateAfterScenario; afterRestoration = $stateAfterRestoration }
    logs = $logs
    restoration = [ordered]@{ attempted = $true; complete = $restorationComplete; mismatches = $restorationMismatches }
}
$evidence | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'qualification-evidence.json') -Encoding utf8
if ($null -ne $failure) { throw $failure }
'Protected exact-archive qualification passed with complete restoration.'
