#requires -Version 7.4
<#
.SYNOPSIS
Rehearses AAML's legacy migrations end-to-end against a production artifact using retained test fixtures.

.DESCRIPTION
Two rehearsals, each in an isolated LOCALAPPDATA environment against the exact release artifact:

Run A (data-root migration): seeds the former application root
"XCOM2 Alternative Mod Launcher" with the 12-item migration manifest (settings,
profiles, snapshots, and logs). Settings documents come from the tracked
compatibility fixtures; profile/snapshot documents follow the current repository
schemas. Then AAML is started and the modern-data-root receipt is verified:
status Completed, every item Copied, destination copies byte-identical, and the
former root preserved.

Run B (legacy settings import): places the legacy launcher settings fixture as
the bundled app-directory settings.json candidate. Then AAML is started and the
legacy-migration-v1.json report is verified: source hash, source preserved,
quick-toggle retention, and the migrated preferences present in the modern
settings document.

Both rehearsals use no real user data and restore the artifact directory.

.PARAMETER ArtifactDirectory
Extracted win-x64 release payload directory containing AAML.Avalonia.exe.

.PARAMETER EvidenceDirectory
Directory where rehearsal evidence (JSON, receipts, logs) is written.

.PARAMETER FixturesDirectory
Compatibility fixtures directory (defaults to the tracked CharacterizationTests assets).

.PARAMETER InitTimeoutSeconds
Maximum seconds to wait for the receipt/report to be produced.

.OUTPUTS
Writes legacy-migration-rehearsal-evidence.json into the evidence directory.
Exit code 0 when both rehearsals pass; 1 otherwise.
#>
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$FixturesDirectory,
    [ValidateRange(30, 900)][int]$InitTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-DirectoryHashes([string]$Root) {
    $result = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $result }
    Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $result[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $result
}

function Stop-AamlProcess($Process) {
    try {
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id))
        if ($null -ne $window) { $null = $Process.CloseMainWindow() }
        if (-not $Process.WaitForExit(10000)) { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
    } catch { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
}

function Wait-ForFile([string]$Path, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return $true }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return (Test-Path -LiteralPath $Path -PathType Leaf)
}

function Wait-ForCompletedReceipt([string]$Path, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $current = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            if ($null -ne $current.completedAtUtc) { return $current }
            $last = $current
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $last
}

$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$executable = Join-Path $artifact 'AAML.Avalonia.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'AAML executable is missing in the artifact directory.' }

$evidence = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidence.StartsWith($artifact + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence directory cannot be inside the artifact.' }
if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Recurse -Force }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($FixturesDirectory)) {
    $FixturesDirectory = Join-Path $PSScriptRoot '..\tests\AAML.Infrastructure.Common.CharacterizationTests\TestAssets\Compatibility'
}
$FixturesDirectory = (Resolve-Path -LiteralPath $FixturesDirectory).Path
$fixturesSettings = Join-Path $FixturesDirectory 'settings'

$steamAppIdPath = Join-Path $artifact 'steam_appid.txt'
$steamAppIdBefore = if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) { (Get-FileHash -LiteralPath $steamAppIdPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$seededAppSettingsPath = Join-Path $artifact 'settings.json'

$results = [ordered]@{ dataRoot = $null; legacyImport = $null }
$startedAt = [DateTimeOffset]::UtcNow

try {
    # ---------------- Run A: data-root migration rehearsal ----------------
    $runA = [ordered]@{ schemaVersion = 1; status = 'failed'; reason = $null; receiptStatus = $null; itemsCopied = 0; itemCount = 0; destinationByteIdentical = $false; formerRootPreserved = $false }
    $processA = $null
    $previousLocalAppData = $env:LOCALAPPDATA; $previousTemp = $env:TEMP; $previousTmp = $env:TMP
    try {
        $sandboxA = Join-Path $evidence 'sandbox-data-root'
        $localAppDataA = Join-Path $sandboxA 'LocalAppData'
        $formerRootA = Join-Path $localAppDataA 'XCOM2 Alternative Mod Launcher'
        $newRootA = Join-Path $localAppDataA 'AAML'
        $receiptPathA = Join-Path $newRootA 'State\Migrations\modern-data-root-v1.json'

        # Seed the former root with the 12-item manifest content.
        New-Item -ItemType Directory -Path (Join-Path $formerRootA 'Config') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $formerRootA 'Data\Profiles') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $formerRootA 'Data\ConfigurationSnapshots') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $formerRootA 'State\Logs') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixturesSettings 'schema-v10.json') -Destination (Join-Path $formerRootA 'Config\settings.json') -Force
        Copy-Item -LiteralPath (Join-Path $fixturesSettings 'schema-v9.json') -Destination (Join-Path $formerRootA 'Config\settings.json.bak') -Force

        $profilesJson = [ordered]@{
            schemaVersion = 1
            profiles = @([ordered]@{
                id = '9f3c1a2e-5b7d-4e6f-9a1b-2c3d4e5f6a7b'
                name = 'Campaign'
                gameVariant = 'XCom2WarOfTheChosen'
                mods = @([ordered]@{ source = 'SteamWorkshop'; packageId = 'AllRegionLinks'; workshopId = 630044970; order = 0 })
                launchArguments = @('-review', '-noRedScreens')
                createdAt = '2026-07-18T20:00:00Z'
                updatedAt = '2026-07-19T12:30:00Z'
            })
        }
        $profilesJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $formerRootA 'Data\Profiles\profiles.json') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $formerRootA 'Data\Profiles\profiles.json') -Destination (Join-Path $formerRootA 'Data\Profiles\profiles.json.bak') -Force

        $snapshotsJson = [ordered]@{
            schemaVersion = 1
            snapshots = @([ordered]@{
                source = 'Manual'
                locationIdentity = 'C:\Mods\One'
                relativePath = 'Config/XCom.ini'
                text = "XComGame.GameDifficulty=ExtraHard`nXComGame.GameMode=Campaign"
                encoding = 'Utf8'
                newLines = 'Lf'
            })
        }
        $snapshotsJson | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $formerRootA 'Data\ConfigurationSnapshots\snapshots.json') -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $formerRootA 'Data\ConfigurationSnapshots\snapshots.json') -Destination (Join-Path $formerRootA 'Data\ConfigurationSnapshots\snapshots.json.bak') -Force

        Set-Content -LiteralPath (Join-Path $formerRootA 'State\Logs\aaml.log') -Value '{"Timestamp":"2026-07-19T12:00:00Z","Level":1,"EventName":"rehearsal.seed","Message":"rehearsal log line."}' -Encoding utf8
        foreach ($i in 1..5) {
            Set-Content -LiteralPath (Join-Path $formerRootA "State\Logs\aaml.log.$i") -Value '{"Timestamp":"2026-07-19T11:00:00Z","Level":1,"EventName":"rehearsal.seed.rotated","Message":"rehearsal rotated log line."}' -Encoding utf8
        }
        $formerHashesBeforeA = Get-DirectoryHashes $formerRootA

        # Launch AAML with the isolated environment; the receipt is written at startup.
        $previousLocalAppData = $env:LOCALAPPDATA; $previousTemp = $env:TEMP; $previousTmp = $env:TMP
        New-Item -ItemType Directory -Path (Join-Path $localAppDataA 'AAML\Config') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $sandboxA 'Temp') -Force | Out-Null
        $env:LOCALAPPDATA = $localAppDataA; $env:TEMP = Join-Path $sandboxA 'Temp'; $env:TMP = Join-Path $sandboxA 'Temp'
        $processA = Start-Process -FilePath $executable -WorkingDirectory $artifact -PassThru
        $receiptA = Wait-ForCompletedReceipt -Path $receiptPathA -TimeoutSeconds $InitTimeoutSeconds
        if ($null -eq $receiptA -or $null -eq $receiptA.completedAtUtc) {
            Stop-AamlProcess $processA
            throw "Data-root migration receipt was not completed within $InitTimeoutSeconds seconds."
        }
        # Halt the app immediately so post-migration writes cannot alter the verified destinations.
        if (-not $processA.HasExited) { Stop-Process -Id $processA.Id -Force -ErrorAction SilentlyContinue }

        # Verify receipt, byte-identical copies, and former-root preservation.
        if ($receiptA.schemaVersion -ne 2 -or $receiptA.expectedManifestCount -ne 12) { throw "Receipt manifest identity is unexpected (schema $($receiptA.schemaVersion), count $($receiptA.expectedManifestCount))." }
        if ($null -eq $receiptA.completedAtUtc) { throw 'Receipt has no completion timestamp; migration did not complete.' }
        $runA.receiptStatus = $receiptA.status
        $runA.itemCount = $receiptA.items.Count
        $runA.itemsCopied = @($receiptA.items | Where-Object { $_.outcome -eq 'Copied' }).Count
        if ($receiptA.status -ne 'Completed') { throw "Receipt status is $($receiptA.status); expected Completed." }
        if ($runA.itemsCopied -ne 12) { throw "Only $($runA.itemsCopied) of 12 items were Copied." }

        $destinationMatches = $true
        foreach ($item in $receiptA.items) {
            if (-not (Test-Path -LiteralPath $item.destination -PathType Leaf)) { $destinationMatches = $false; break }
            $actual = (Get-FileHash -LiteralPath $item.destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $item.sha256) { $destinationMatches = $false; break }
        }
        $runA.destinationByteIdentical = $destinationMatches
        if (-not $destinationMatches) { throw 'Destination copies do not match the receipt hashes.' }
        $formerHashesAfterA = Get-DirectoryHashes $formerRootA
        $runA.formerRootPreserved = (($formerHashesBeforeA | ConvertTo-Json -Compress) -ceq ($formerHashesAfterA | ConvertTo-Json -Compress))
        if (-not $runA.formerRootPreserved) { throw 'Former application root was modified by the migration.' }

        Copy-Item -LiteralPath $receiptPathA -Destination (Join-Path $evidence 'data-root-migration-receipt.json') -Force
        if (Test-Path -LiteralPath (Join-Path $newRootA 'State\Logs\aaml.log')) { Copy-Item -LiteralPath (Join-Path $newRootA 'State\Logs\aaml.log') -Destination (Join-Path $evidence 'aaml-data-root.log') -Force }
        Stop-AamlProcess $processA
        $runA.status = 'passed'
    }
    catch {
        $runA.status = 'failed'; $runA.reason = $_.Exception.Message
        if ($null -ne $processA) { try { if (-not $processA.HasExited) { Stop-AamlProcess $processA } } catch { } }
    }
    finally {
        $env:LOCALAPPDATA = $previousLocalAppData; $env:TEMP = $previousTemp; $env:TMP = $previousTmp
        $results.dataRoot = $runA
    }

    # ---------------- Run B: legacy settings import rehearsal ----------------
    $runB = [ordered]@{ schemaVersion = 1; status = 'failed'; reason = $null; reportFound = $false; sourceHashMatch = $false; sourcePreserved = $false; quickToggleRetained = $false; migratedPreferencesVerified = $false }
    $processB = $null
    $previousLocalAppData = $env:LOCALAPPDATA; $previousTemp = $env:TEMP; $previousTmp = $env:TMP
    try {
        $sandboxB = Join-Path $evidence 'sandbox-legacy-import'
        $localAppDataB = Join-Path $sandboxB 'LocalAppData'
        $newRootB = Join-Path $localAppDataB 'AAML'
        $reportPathB = Join-Path $newRootB 'Config\legacy-migration-v1.json'
        $modernSettingsB = Join-Path $newRootB 'Config\settings.json'
        $legacyFixture = Join-Path $fixturesSettings 'legacy-preferences.json'
        $legacyFixtureHash = (Get-FileHash -LiteralPath $legacyFixture -Algorithm SHA256).Hash.ToLowerInvariant()

        # Seed the bundled app-directory legacy settings candidate.
        Copy-Item -LiteralPath $legacyFixture -Destination $seededAppSettingsPath -Force

        $previousLocalAppData = $env:LOCALAPPDATA; $previousTemp = $env:TEMP; $previousTmp = $env:TMP
        New-Item -ItemType Directory -Path (Join-Path $localAppDataB 'AAML\Config') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $sandboxB 'Temp') -Force | Out-Null
        $env:LOCALAPPDATA = $localAppDataB; $env:TEMP = Join-Path $sandboxB 'Temp'; $env:TMP = Join-Path $sandboxB 'Temp'
        $processB = Start-Process -FilePath $executable -WorkingDirectory $artifact -PassThru
        $ready = Wait-ForFile -Path $reportPathB -TimeoutSeconds $InitTimeoutSeconds
        if (-not $ready) { Stop-AamlProcess $processB; throw "Legacy migration report was not produced within $InitTimeoutSeconds seconds." }
        $reportB = Get-Content -LiteralPath $reportPathB -Raw | ConvertFrom-Json

        $runB.reportFound = $true
        $runB.sourceHashMatch = ($reportB.sourceSha256 -ne $null) -and ($reportB.sourceSha256.ToLowerInvariant() -eq $legacyFixtureHash)
        $runB.sourcePreserved = [bool]$reportB.sourcePreserved
        $runB.quickToggleRetained = @($reportB.quickToggleArguments).Count -gt 0
        if (-not $runB.sourceHashMatch) { throw 'Report source hash does not match the seeded legacy fixture.' }
        if (-not $runB.sourcePreserved) { throw 'Report claims the legacy source was not preserved.' }
        if (-not $runB.quickToggleRetained) { throw 'Report did not retain the legacy quick-toggle arguments.' }

        if (Test-Path -LiteralPath $modernSettingsB -PathType Leaf) {
            $modernB = Get-Content -LiteralPath $modernSettingsB -Raw | ConvertFrom-Json
            $checks = @(
                ($modernB.theme -eq 'Dark')
                ($modernB.checkForUpdates -eq $false)
                ($modernB.closeAfterLaunch -eq $true)
                ($modernB.allowMultipleInstances -eq $true)
                (@($modernB.launchArguments) -contains '-review')
                (@($modernB.launchArguments) -contains '-allowConsole')
            )
            $runB.migratedPreferencesVerified = (-not ($checks | Where-Object { -not $_ }))
            if (-not $runB.migratedPreferencesVerified) { throw 'Migrated modern settings do not reflect the legacy fixture preferences.' }
        }
        else { throw 'Modern settings document was not created from the legacy import.' }

        Copy-Item -LiteralPath $reportPathB -Destination (Join-Path $evidence 'legacy-migration-report.json') -Force
        Stop-AamlProcess $processB
        $runB.status = 'passed'
    }
    catch {
        $runB.status = 'failed'; $runB.reason = $_.Exception.Message
        if ($null -ne $processB) { try { if (-not $processB.HasExited) { Stop-AamlProcess $processB } } catch { } }
    }
    finally {
        $env:LOCALAPPDATA = $previousLocalAppData; $env:TEMP = $previousTemp; $env:TMP = $previousTmp
        if (Test-Path -LiteralPath $seededAppSettingsPath -PathType Leaf) { Remove-Item -LiteralPath $seededAppSettingsPath -Force -ErrorAction SilentlyContinue }
        $results.legacyImport = $runB
    }
}
finally {
    # Restore artifact directory state (steam_appid.txt).
    if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) {
        $steamAppIdNow = (Get-FileHash -LiteralPath $steamAppIdPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($steamAppIdNow -ne $steamAppIdBefore) { Remove-Item -LiteralPath $steamAppIdPath -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $seededAppSettingsPath -PathType Leaf) { Remove-Item -LiteralPath $seededAppSettingsPath -Force -ErrorAction SilentlyContinue }
}

$summary = [ordered]@{
    schemaVersion = 1
    product = 'Avalonia Alternative Mod Launcher'
    artifactDirectory = $artifact
    fixturesDirectory = $FixturesDirectory
    startedAtUtc = $startedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    dataRootMigration = $results.dataRoot
    legacySettingsImport = $results.legacyImport
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'legacy-migration-rehearsal-evidence.json') -Encoding utf8

Write-Output ('Data-root migration rehearsal: {0}' -f $results.dataRoot.status)
if ($results.dataRoot.reason) { Write-Output "  reason: $($results.dataRoot.reason)" }
Write-Output ('Legacy settings import rehearsal: {0}' -f $results.legacyImport.status)
if ($results.legacyImport.reason) { Write-Output "  reason: $($results.legacyImport.reason)" }

if ($results.dataRoot.status -ne 'passed' -or $results.legacyImport.status -ne 'passed') { exit 1 }
exit 0
