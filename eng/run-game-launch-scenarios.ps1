#requires -Version 7.4
<#
.SYNOPSIS
Runs AAML game-launch scenarios from an extracted Windows release artifact against real installed games.

.DESCRIPTION
For each variant (XCom2, XCom2WarOfTheChosen, XCom2WarOfTheChosenChallengeMode, ChimeraSquad):
 - Starts AAML from the extracted artifact in an isolated LOCALAPPDATA/TEMP environment with
   pre-seeded settings (selected variant, real installation location, no mods, no update checks,
   manual workshop policy).
 - Waits for full application initialization (application.initialization_completed in the app log).
 - Invokes DashboardLaunchButton through UI Automation.
 - Verifies the expected game process started from the expected executable path and that
   game.launch_completed was logged for that variant.
 - Restores the game-owned configuration directory that AAML legitimately rewrites and verifies
   the byte-for-byte restoration.
Variants whose installation or executable is absent are recorded explicitly as "unavailable"
instead of being inferred from source-build or other-variant evidence.

.PARAMETER ArtifactDirectory
Extracted win-x64 release payload directory containing AAML.Avalonia.exe.

.PARAMETER EvidenceDirectory
Directory where scenario evidence (JSON and logs) is written.

.PARAMETER XCom2Install
Steam installation root for XCOM 2 (Vanilla / War of the Chosen / Challenge Mode).

.PARAMETER ChimeraSquadInstall
Steam installation root for XCOM: Chimera Squad.

.PARAMETER InitTimeoutSeconds
Maximum seconds to wait for AAML to fully initialize.

.PARAMETER GameTimeoutSeconds
Maximum seconds to wait for the game process to appear.

.OUTPUTS
Writes game-launch-scenarios-evidence.json into the evidence directory.
Exit code 0 when every available scenario passed; 1 otherwise.
#>
param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$XCom2Install,
    [string]$ChimeraSquadInstall,
    [ValidateRange(30, 900)][int]$InitTimeoutSeconds = 180,
    [ValidateRange(30, 900)][int]$GameTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ByAutomationId($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ByAutomationId($Root, [string]$AutomationId, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-ByAutomationId $Root $AutomationId
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "UI Automation element '$AutomationId' was not found within $TimeoutSeconds seconds."
}

function Invoke-Element($Element) {
    $invoke = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        ([System.Windows.Automation.InvokePattern]$invoke).Invoke()
        return
    }
    throw "Element '$($Element.Current.AutomationId)' does not expose InvokePattern."
}

function Get-DirectoryHashes([string]$Root) {
    $result = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $result }
    Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $result[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $result
}

function New-IsolatedSettings([string]$Base, [hashtable]$VariantRoots, [string]$SelectedGame, [string]$GameLocation) {
    $localAppData = Join-Path $Base 'LocalAppData'
    $temp = Join-Path $Base 'Temp'
    New-Item -ItemType Directory -Path (Join-Path $localAppData 'AAML\Config') -Force | Out-Null
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $settings = [ordered]@{
        schemaVersion = 10
        selectedGame = $SelectedGame
        gameInstallationLocation = $GameLocation
        modRootLocations = @()
        launchArguments = @()
        modIntents = @()
        categories = @()
        tags = @()
        allowLaunchWithMissingDependencies = $false
        gameLocations = @(
            [ordered]@{ game = 'XCom2'; installationLocation = $VariantRoots['XCom2']; modRootLocations = @() },
            [ordered]@{ game = 'XCom2WarOfTheChosen'; installationLocation = $VariantRoots['XCom2']; modRootLocations = @() },
            [ordered]@{ game = 'XCom2WarOfTheChosenChallengeMode'; installationLocation = $VariantRoots['XCom2']; modRootLocations = @() },
            [ordered]@{ game = 'ChimeraSquad'; installationLocation = $VariantRoots['ChimeraSquad']; modRootLocations = @() }
        )
        closeAfterLaunch = $false
        workshopStartupRefresh = 'Manual'
        theme = 'System'
        allowMultipleInstances = $true
        duplicatePreferences = @()
        modGrid = [ordered]@{ includeHidden = $false; stateFilter = $null; groupByCategory = $false; collapsedGroups = @() }
        retainedWorkshopItems = @()
        checkForUpdates = $false
        updateChannel = 'Stable'
        navigationRailMode = 'Expanded'
        autoSaveChanges = $false
        textScale = 1.0
        iconScale = 1.0
    }
    $settingsPath = Join-Path $localAppData 'AAML\Config\settings.json'
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    [pscustomobject]@{
        LocalAppData = $localAppData
        Temp = $temp
        SettingsPath = $settingsPath
        LogPath = Join-Path $localAppData 'AAML\State\Logs\aaml.log'
    }
}

$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$executable = Join-Path $artifact 'AAML.Avalonia.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'AAML executable is missing in the artifact directory.' }

$evidence = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidence.StartsWith($artifact + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence directory cannot be inside the artifact.' }
if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Recurse -Force }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

$documentsRoot = [Environment]::GetFolderPath('MyDocuments')
$variantRoots = [ordered]@{ XCom2 = $XCom2Install; ChimeraSquad = $ChimeraSquadInstall }

$scenarios = @(
    [pscustomobject]@{ Variant = 'XCom2'; InstallRoot = $XCom2Install; ExeRelativePath = 'Binaries\Win64\XCom2.exe'; ProcessName = 'XCom2'; ConfigGameFolder = 'XCOM2' },
    [pscustomobject]@{ Variant = 'XCom2WarOfTheChosen'; InstallRoot = $XCom2Install; ExeRelativePath = 'XCom2-WarOfTheChosen\Binaries\Win64\XCom2.exe'; ProcessName = 'XCom2'; ConfigGameFolder = 'XCOM2 War of the Chosen' },
    [pscustomobject]@{ Variant = 'XCom2WarOfTheChosenChallengeMode'; InstallRoot = $XCom2Install; ExeRelativePath = 'XCom2-WarOfTheChosen\Binaries\Win64\XCom2.exe'; ProcessName = 'XCom2'; ConfigGameFolder = 'XCOM2 War of the Chosen' },
    [pscustomobject]@{ Variant = 'ChimeraSquad'; InstallRoot = $ChimeraSquadInstall; ExeRelativePath = 'Binaries\Win64\xcom.exe'; ProcessName = 'xcom'; ConfigGameFolder = 'XCOM Chimera Squad' }
)

$results = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow

foreach ($scenario in $scenarios) {
    $scenarioResult = [ordered]@{
        schemaVersion = 1
        variant = $scenario.Variant
        status = 'failed'
        reason = $null
        executable = $null
        gameProcessId = $null
        gameProcessPath = $null
        aamlProcessId = $null
        initializationCompletedAtUtc = $null
        launchCompletedAtUtc = $null
        configurationRestored = $null
        startedAtUtc = [DateTimeOffset]::UtcNow
        completedAtUtc = $null
    }
    $process = $null
    $gameProcess = $null
    $envSaved = $false
    $previousLocalAppData = $null
    $previousTemp = $null
    $previousTmp = $null
    $steamAppIdPath = Join-Path $artifact 'steam_appid.txt'
    $steamAppIdBefore = if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) { (Get-FileHash -LiteralPath $steamAppIdPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    $configDir = Join-Path $documentsRoot 'My Games' $scenario.ConfigGameFolder 'XComGame\Config'
    $configBackup = Join-Path $evidence "sandbox-$($scenario.Variant)\config-backup"
    $configExistedBefore = Test-Path -LiteralPath $configDir -PathType Container
    $configHashesBefore = [ordered]@{}

    try {
        $exePath = if (-not [string]::IsNullOrWhiteSpace($scenario.InstallRoot)) { Join-Path $scenario.InstallRoot $scenario.ExeRelativePath } else { $null }
        $scenarioResult.executable = $exePath
        if ($null -eq $exePath -or -not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
            $scenarioResult.status = 'unavailable'
            $scenarioResult.reason = 'Game installation or executable is not present on this host; recorded explicitly instead of inferred.'
            continue
        }

        $sandbox = Join-Path $evidence "sandbox-$($scenario.Variant)"

        # Snapshot the real game-owned config so AAML's legitimate rewrite can be rolled back.
        if ($configExistedBefore) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $configBackup) -Force | Out-Null
            Copy-Item -LiteralPath $configDir -Destination $configBackup -Recurse -Force
            $configHashesBefore = Get-DirectoryHashes $configDir
        }

        # Snapshot already-running game processes so only a newly started one counts.
        $preexistingGamePids = @(Get-Process -Name $scenario.ProcessName -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

        $envInfo = New-IsolatedSettings -Base $sandbox -VariantRoots $variantRoots -SelectedGame $scenario.Variant -GameLocation $scenario.InstallRoot
        $previousLocalAppData = $env:LOCALAPPDATA
        $previousTemp = $env:TEMP
        $previousTmp = $env:TMP
        $env:LOCALAPPDATA = $envInfo.LocalAppData
        $env:TEMP = $envInfo.Temp
        $env:TMP = $envInfo.Temp
        $envSaved = $true

        $process = Start-Process -FilePath $executable -WorkingDirectory $artifact -PassThru
        $scenarioResult.aamlProcessId = $process.Id

        # Wait for full initialization: window present AND initialization_completed logged.
        $initDeadline = [DateTimeOffset]::UtcNow.AddSeconds($InitTimeoutSeconds)
        $window = $null
        $initComplete = $false
        do {
            if ($process.HasExited) { throw "AAML exited before initialization with code $($process.ExitCode)." }
            $condition = [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Avalonia Alternative Mod Launcher'))
            $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
            if (Test-Path -LiteralPath $envInfo.LogPath -PathType Leaf) {
                $initComplete = [bool](Select-String -LiteralPath $envInfo.LogPath -Pattern 'application.initialization_completed' -Quiet)
            }
            if ($null -ne $window -and $initComplete) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $initDeadline)
        if ($null -eq $window -or -not $initComplete) { throw "AAML did not complete initialization within $InitTimeoutSeconds seconds (windowPresent=$($null -ne $window), initComplete=$initComplete)." }
        $scenarioResult.initializationCompletedAtUtc = [DateTimeOffset]::UtcNow

        # Invoke launch only after full initialization so LaunchAsync sees loaded settings.
        $launchButton = Wait-ByAutomationId -Root $window -AutomationId 'DashboardLaunchButton' -TimeoutSeconds 30
        Invoke-Element $launchButton

        # Wait for a NEW game process from the expected executable.
        $gameDeadline = [DateTimeOffset]::UtcNow.AddSeconds($GameTimeoutSeconds)
        $gameFound = $null
        do {
            $gameFound = @(Get-Process -Name $scenario.ProcessName -ErrorAction SilentlyContinue | Where-Object {
                if ($preexistingGamePids -contains $_.Id) { return $false }
                $pathOk = $true
                try {
                    if ($null -ne $_.Path) { $pathOk = $_.Path.TrimEnd('\').ToLowerInvariant() -eq $exePath.TrimEnd('\').ToLowerInvariant() }
                } catch { }
                $pathOk
            } | Select-Object -First 1)
            if ($gameFound.Count -gt 0) { break }
            if ($process.HasExited) { throw "AAML exited (code $($process.ExitCode)) before the game process started." }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $gameDeadline)
        if ($gameFound.Count -eq 0) { throw "Game process '$($scenario.ProcessName)' from '$exePath' did not start within $GameTimeoutSeconds seconds." }
        $gameProcess = $gameFound[0]
        $scenarioResult.gameProcessId = $gameProcess.Id
        try { $scenarioResult.gameProcessPath = $gameProcess.Path } catch { }

        # Verify the launch completion log for this variant.
        $launchLogDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        $launchLogged = $false
        do {
            if (Test-Path -LiteralPath $envInfo.LogPath -PathType Leaf) {
                $launchLogged = [bool](Select-String -LiteralPath $envInfo.LogPath -Pattern ("game\.launch_completed.*Started {0} with" -f [regex]::Escape($scenario.Variant)) -Quiet)
            }
            if ($launchLogged) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTimeOffset]::UtcNow -lt $launchLogDeadline)
        if (-not $launchLogged) { throw 'game.launch_completed was not logged for this variant.' }
        $scenarioResult.launchCompletedAtUtc = [DateTimeOffset]::UtcNow

        # Stop the game (children first).
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($gameProcess.Id)" -ErrorAction SilentlyContinue
        foreach ($child in $children) { Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id $gameProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        # Restore the game-owned configuration directory and verify byte-for-byte restoration.
        if (Test-Path -LiteralPath $configDir -PathType Container) {
            Remove-Item -LiteralPath $configDir -Recurse -Force
        }
        if ($configExistedBefore) {
            Copy-Item -LiteralPath $configBackup -Destination $configDir -Recurse -Force
        }
        $configHashesAfter = Get-DirectoryHashes $configDir
        if (($configHashesBefore | ConvertTo-Json -Compress) -cne ($configHashesAfter | ConvertTo-Json -Compress)) {
            throw 'Game configuration directory was not restored to its pre-launch state.'
        }
        $scenarioResult.configurationRestored = $true

        # Close AAML gracefully.
        try {
            $window2 = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id))
            if ($null -ne $window2) { $null = $process.CloseMainWindow() }
            if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        } catch { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }

        # Remove steam_appid.txt if AAML created it.
        $steamAppIdAfter = if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) { (Get-FileHash -LiteralPath $steamAppIdPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        if ($null -ne $steamAppIdAfter -and $steamAppIdAfter -ne $steamAppIdBefore) { Remove-Item -LiteralPath $steamAppIdPath -Force }

        if (Test-Path -LiteralPath $envInfo.LogPath -PathType Leaf) {
            Copy-Item -LiteralPath $envInfo.LogPath -Destination (Join-Path $evidence "aaml-$($scenario.Variant).log") -Force
        }
        $scenarioResult.status = 'passed'
    }
    catch {
        $scenarioResult.status = 'failed'
        $scenarioResult.reason = $_.Exception.Message
        if ($null -ne $gameProcess) {
            try { if (-not $gameProcess.HasExited) { Stop-Process -Id $gameProcess.Id -Force -ErrorAction SilentlyContinue } } catch { }
        }
        if ($null -ne $process) {
            try { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } } catch { }
        }
        # Best-effort steam_appid.txt cleanup after a failure.
        if (Test-Path -LiteralPath $steamAppIdPath -PathType Leaf) {
            $steamAppIdNow = (Get-FileHash -LiteralPath $steamAppIdPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($steamAppIdNow -ne $steamAppIdBefore) { Remove-Item -LiteralPath $steamAppIdPath -Force -ErrorAction SilentlyContinue }
        }
        # Best-effort configuration restoration after a failure.
        if ($configExistedBefore -and (Test-Path -LiteralPath $configBackup -PathType Container -ErrorAction SilentlyContinue)) {
            if (Test-Path -LiteralPath $configDir -PathType Container -ErrorAction SilentlyContinue) { Remove-Item -LiteralPath $configDir -Recurse -Force -ErrorAction SilentlyContinue }
            Copy-Item -LiteralPath $configBackup -Destination $configDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    finally {
        if ($envSaved) {
            $env:LOCALAPPDATA = $previousLocalAppData
            $env:TEMP = $previousTemp
            $env:TMP = $previousTmp
        }
        $scenarioResult.completedAtUtc = [DateTimeOffset]::UtcNow
        $results.Add([pscustomobject]$scenarioResult)
        Write-Output ("{0,-42} {1,-12} {2}" -f $scenario.Variant, $scenarioResult.status, $(if ($scenarioResult.reason) { $scenarioResult.reason } else { '' }))
    }
}

$summary = [ordered]@{
    schemaVersion = 1
    product = 'Avalonia Alternative Mod Launcher'
    artifactDirectory = $artifact
    executable = $executable
    startedAtUtc = $startedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    scenarios = @($results)
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'game-launch-scenarios-evidence.json') -Encoding utf8

$failed = @($results | Where-Object { $_.status -eq 'failed' })
$passed = @($results | Where-Object { $_.status -eq 'passed' })
$unavailable = @($results | Where-Object { $_.status -eq 'unavailable' })
Write-Output ''
Write-Output ("Scenarios: {0} passed, {1} unavailable, {2} failed" -f $passed.Count, $unavailable.Count, $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
