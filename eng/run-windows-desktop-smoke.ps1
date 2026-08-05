param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [string]$ExpectedTitle = 'Avalonia Alternative Mod Launcher',
    [ValidateRange(5, 120)][int]$StartupTimeoutSeconds = 30,
    [ValidateRange(1, 30)][int]$StepTimeoutSeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-TreeHashes([string]$Root) {
    $result = [ordered]@{}
    Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        $result[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $result
}

function Assert-Checksums([string]$Root) {
    $checksumPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'Staged artifact has no SHA256SUMS file.' }
    $checksummed = @()
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw "Invalid checksum line: $line" }
        $checksummed += $Matches[2]
        $path = Join-Path $Root $Matches[2]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checksummed file is missing: $($Matches[2])" }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ne $Matches[1]) { throw "Checksum mismatch: $($Matches[2])" }
    }
    $packaged = @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object { [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') })
    if (@(Compare-Object $packaged $checksummed).Count -ne 0) { throw 'SHA256SUMS does not exactly cover the staged artifact.' }
}

function Assert-TreeEqual($Expected, $Actual, [string]$Description) {
    if (($Expected | ConvertTo-Json -Depth 10 -Compress) -cne ($Actual | ConvertTo-Json -Depth 10 -Compress)) {
        throw "$Description changed during smoke execution."
    }
}

function Get-DurableHashes([string]$Root) {
    $result = [ordered]@{}
    foreach ($directory in @('Config', 'Data')) {
        $path = Join-Path $Root $directory
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        foreach ($entry in (Get-TreeHashes $path).GetEnumerator()) { $result["$directory/$($entry.Key)"] = $entry.Value }
    }
    return $result
}

function Find-ByAutomationId($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ByAutomationId($Root, [string]$AutomationId, [int]$TimeoutSeconds = $StepTimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-ByAutomationId $Root $AutomationId
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "UI Automation element '$AutomationId' was not found within $TimeoutSeconds seconds."
}

function Get-ElementText($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
    }
    return $Element.Current.Name
}

function Set-ElementValue($Element, [string]$Value) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        throw "Element '$($Element.Current.AutomationId)' does not expose ValuePattern."
    }
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

function Invoke-Element($Element) {
    $scroll = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) {
        ([System.Windows.Automation.ScrollItemPattern]$scroll).ScrollIntoView()
    }
    $selection = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        ([System.Windows.Automation.SelectionItemPattern]$selection).Select()
        return
    }
    $invoke = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        ([System.Windows.Automation.InvokePattern]$invoke).Invoke()
        return
    }
    throw "Element '$($Element.Current.AutomationId)' exposes neither SelectionItemPattern nor InvokePattern."
}

function Wait-ForText($Root, [string]$Expected, [int]$TimeoutSeconds = $StepTimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            if ($element.Current.Name.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) { return $element }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Expected UI text '$Expected' was not observed within $TimeoutSeconds seconds."
}

function Dismiss-Notification($Root) {
    foreach ($name in @('OK', 'Close')) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $button = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $button) { Invoke-Element $button; return }
    }
}

function Save-WindowScreenshot($Window, [string]$Path) {
    try {
        Add-Type -AssemblyName System.Drawing
        $bounds = $Window.Current.BoundingRectangle
        if ($bounds.Width -le 0 -or $bounds.Height -le 0) { return $null }
        $bitmap = [System.Drawing.Bitmap]::new([int][Math]::Ceiling($bounds.Width), [int][Math]::Ceiling($bounds.Height))
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
                $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $graphics.Dispose() }
        }
        finally { $bitmap.Dispose() }
        return [System.IO.Path]::GetFileName($Path)
    }
    catch { return $null }
}

function Export-AutomationTree($Root, [string]$Path) {
    $rows = [System.Collections.Generic.List[object]]::new()
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    function Visit($Element, [int]$Depth) {
        if ($rows.Count -ge 2000) { return }
        $rows.Add([ordered]@{
            depth = $Depth
            controlType = $Element.Current.ControlType.ProgrammaticName
            automationId = $Element.Current.AutomationId
            name = $Element.Current.Name
            enabled = $Element.Current.IsEnabled
            offscreen = $Element.Current.IsOffscreen
        })
        $child = $walker.GetFirstChild($Element)
        while ($null -ne $child) { Visit $child ($Depth + 1); $child = $walker.GetNextSibling($child) }
    }
    Visit $Root 0
    $rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8
}

$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$executable = Join-Path $artifact 'AAML.Avalonia.exe'
$metadataPath = Join-Path $artifact 'release-metadata.json'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Staged AAML executable is missing.' }
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw 'Staged release metadata is missing.' }
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.rid -ne 'win-x64' -or -not $metadata.selfContained) { throw 'Smoke requires a self-contained win-x64 staged artifact.' }
Assert-Checksums $artifact
$artifactHashesBefore = Get-TreeHashes $artifact

$evidence = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidence.StartsWith($artifact + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence directory cannot be inside the staged artifact.' }
if (Test-Path -LiteralPath $evidence) { Remove-Item -LiteralPath $evidence -Recurse -Force }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$screenshots = Join-Path $evidence 'screenshots'
New-Item -ItemType Directory -Path $screenshots -Force | Out-Null
$sandbox = Join-Path $evidence 'sandbox'
$localAppData = Join-Path $sandbox 'LocalAppData'
$temp = Join-Path $sandbox 'Temp'
$newRoot = Join-Path $localAppData 'AAML'
$oldRoot = Join-Path $localAppData 'XCOM2 Alternative Mod Launcher'
$settingsDirectory = Join-Path $newRoot 'Config'
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $temp -Force | Out-Null

$settings = [ordered]@{
    schemaVersion = 9; selectedGame = 'XCom2'; gameInstallationLocation = $null; modRootLocations = @()
    launchArguments = @('-review'); modIntents = @(); categories = @(); tags = @()
    allowLaunchWithMissingDependencies = $false; gameLocations = @([ordered]@{ game = 'XCom2'; installationLocation = $null; modRootLocations = @() }); closeAfterLaunch = $false
    workshopStartupRefresh = 'AllMods'; theme = 'System'; allowMultipleInstances = $true
    duplicatePreferences = @(); modGrid = [ordered]@{ includeHidden = $false; stateFilter = $null; groupByCategory = $false; collapsedGroups = @() }
    retainedWorkshopItems = @(); checkForUpdates = $false; updateChannel = 'Stable'; navigationRailMode = 'Expanded'; autoSaveChanges = $false
}
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsPath -Encoding utf8
$seedHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$oldRootBefore = if (Test-Path -LiteralPath $oldRoot) { Get-TreeHashes $oldRoot } else { [ordered]@{} }

$previousLocalAppData = $env:LOCALAPPDATA
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$process = $null
$window = $null
$receiptPath = $null
$logPath = $null
$steps = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow
$result = 'failed'
$failure = $null
$artifactHashesAfter = $null

function Invoke-SmokeStep([string]$Name, [scriptblock]$Action) {
    $stepStarted = [DateTimeOffset]::UtcNow
    try {
        $details = & $Action
        $shotName = ($Name -replace '[^A-Za-z0-9.-]', '-') + '.png'
        $shot = Save-WindowScreenshot $window (Join-Path $screenshots $shotName)
        $steps.Add([ordered]@{ name = $Name; result = 'passed'; startedAtUtc = $stepStarted; completedAtUtc = [DateTimeOffset]::UtcNow; screenshot = $shot; details = $details })
    }
    catch {
        $steps.Add([ordered]@{ name = $Name; result = 'failed'; startedAtUtc = $stepStarted; completedAtUtc = [DateTimeOffset]::UtcNow; error = $_.Exception.Message })
        throw
    }
}

try {
    $env:LOCALAPPDATA = $localAppData
    $env:TEMP = $temp
    $env:TMP = $temp
    $process = Start-Process -FilePath $executable -WorkingDirectory $artifact -PassThru
    $receiptPath = Join-Path $newRoot 'State\Migrations\modern-data-root-v1.json'
    $logPath = Join-Path $newRoot 'State\Logs\aaml.log'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        if ($process.HasExited) { throw "AAML exited before readiness with code $($process.ExitCode)." }
        $condition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $ExpectedTitle))
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        $hasLocalEvidence = (Test-Path -LiteralPath $receiptPath -PathType Leaf) -or (Test-Path -LiteralPath $logPath -PathType Leaf)
        if ($null -ne $window -and $hasLocalEvidence) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $window) { throw "AAML top-level window was not found through UI Automation within $StartupTimeoutSeconds seconds." }

    Invoke-SmokeStep 'startup-and-isolation' {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($receipt.schemaVersion -ne 2) { throw "Migration receipt schema is $($receipt.schemaVersion)." }
        if ($receipt.expectedManifestVersion -ne 1 -or $receipt.expectedManifestCount -ne 12) { throw 'Migration receipt manifest identity is unexpected.' }
        if ($receipt.status -notin @('Completed', 'CompletedWithConflicts')) { throw "Migration receipt status is $($receipt.status)." }
        if ($null -eq $receipt.completedAtUtc) { throw 'Migration receipt has no completion timestamp.' }
        if (@($receipt.items | Where-Object { $_.outcome -notin @('SourceMissing', 'DestinationOnly') }).Count -ne 0) { throw 'Former-root migration was not a no-op.' }
        if (Test-Path -LiteralPath $oldRoot) { throw 'Former application root was created or modified.' }
        [ordered]@{ windowTitle = $window.Current.Name; processId = $process.Id; migrationStatus = $receipt.status }
    }

    Invoke-SmokeStep 'dashboard-save-preferences' {
        $page = Wait-ByAutomationId $window 'DashboardPage'
        $status = Wait-ByAutomationId $page 'DashboardStatus'
        $arguments = Wait-ByAutomationId $page 'DashboardLaunchArgumentsTextBox'
        $null = Wait-ByAutomationId $page 'DashboardAutoSaveToggle'
        $savePreferences = Wait-ByAutomationId $page 'DashboardSavePreferencesButton'
        $arguments.SetFocus()
        Set-ElementValue $arguments "-review`r`n-noRedScreens"
        $savePreferences.SetFocus()
        Start-Sleep -Milliseconds 150 # Allow Avalonia's UI Automation value change to reach the two-way binding.
        Invoke-Element $savePreferences
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StepTimeoutSeconds)
        do {
            $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if (@($saved.launchArguments) -contains '-noRedScreens') { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        if (@($saved.launchArguments) -notcontains '-noRedScreens') { throw 'Saved preferences did not persist to the isolated settings root.' }
        if ($saved.checkForUpdates) { throw 'The isolated no-network update preference changed unexpectedly.' }
        [ordered]@{ page = $page.Current.AutomationId; status = Get-ElementText $status; persistedLaunchArgument = '-noRedScreens'; updateChecksEnabled = $saved.checkForUpdates }
    }

    $sections = @(
        [ordered]@{ name = 'Mods'; nav = 'ShellSectionMods'; page = 'ModsPage'; controls = @('ModsRefreshButton', 'ModsSearchTextBox', 'ModsGrid', 'ModsStatus'); invoke = 'ModsRefreshButton' },
        [ordered]@{ name = 'Conflicts'; nav = 'ShellSectionConflicts'; page = 'ConflictsPage'; controls = @('ConflictsRefreshButton', 'ConflictsSearchTextBox', 'ConflictsStatus'); invoke = 'ConflictsRefreshButton' },
        [ordered]@{ name = 'Configurations'; nav = 'ShellSectionConfigurations'; page = 'ConfigurationsPage'; controls = @('ConfigurationsOpenButton', 'ConfigurationsRefreshButton', 'ConfigurationsStatus'); invoke = 'ConfigurationsRefreshButton' }
    )
    foreach ($section in $sections) {
        Invoke-SmokeStep ("section-" + $section.name.ToLowerInvariant()) {
            Invoke-Element (Wait-ByAutomationId $window $section.nav)
            $page = Wait-ByAutomationId $window $section.page
            foreach ($id in $section.controls) { $null = Wait-ByAutomationId $page $id }
            Invoke-Element (Wait-ByAutomationId $page $section.invoke)
            [ordered]@{ navigation = $section.nav; page = $page.Current.AutomationId; verifiedControls = $section.controls; invoked = $section.invoke }
        }
    }

    Invoke-SmokeStep 'section-profiles-failures' {
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionProfiles')
        $page = Wait-ByAutomationId $window 'ProfilesPage'
        $null = Wait-ByAutomationId $page 'ProfilesNameTextBox'
        $status = Wait-ByAutomationId $page 'ProfilesStatus'
        $initialStatus = Get-ElementText $status
        $before = Get-DurableHashes $newRoot
        Invoke-Element (Wait-ByAutomationId $page 'ProfilesApplyButton')
        Start-Sleep -Milliseconds 300
        Assert-TreeEqual $before (Get-DurableHashes $newRoot) 'Profile selection validation'
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionProfiles')
        $page = Wait-ByAutomationId $window 'ProfilesPage'
        Invoke-Element (Wait-ByAutomationId $page 'ProfilesConfirmLegacyButton')
        Start-Sleep -Milliseconds 300
        Assert-TreeEqual $before (Get-DurableHashes $newRoot) 'Legacy profile preview validation'
        [ordered]@{ page = 'ProfilesPage'; initialStatus = $initialStatus; invoked = @('ProfilesApplyButton', 'ProfilesConfirmLegacyButton'); validatedNoMutation = @('selection-required', 'legacy-preview-required') }
    }

    Invoke-SmokeStep 'section-migration-failure' {
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionMigration')
        $page = Wait-ByAutomationId $window 'MigrationPage'
        $report = Wait-ByAutomationId $page 'MigrationReport'
        $initialReport = Get-ElementText $report
        $before = Get-DurableHashes $newRoot
        Invoke-Element (Wait-ByAutomationId $page 'MigrationConfirmActiveModsButton')
        Start-Sleep -Milliseconds 300
        Assert-TreeEqual $before (Get-DurableHashes $newRoot) 'Migration preview validation'
        [ordered]@{ page = 'MigrationPage'; report = $initialReport; invoked = 'MigrationConfirmActiveModsButton'; validatedNoMutation = 'preview-required' }
    }

    Invoke-SmokeStep 'section-support-copy-report' {
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionSupport')
        $page = Wait-ByAutomationId $window 'SupportPage'
        $updateStatus = Wait-ByAutomationId $page 'SupportUpdateStatus'
        Invoke-Element (Wait-ByAutomationId $page 'SupportCopyReportButton')
        Start-Sleep -Milliseconds 300
        $clipboardResult = 'unavailable'
        try {
            $clipboard = Get-Clipboard -Raw -ErrorAction Stop
            if ($clipboard -and $clipboard.Contains('Avalonia Alternative Mod Launcher', [StringComparison]::Ordinal)) { $clipboardResult = 'verified' }
        }
        catch { $clipboardResult = 'unavailable' }
        [ordered]@{ page = $page.Current.AutomationId; updateStatus = Get-ElementText $updateStatus; updateCommandInvoked = $false; clipboard = $clipboardResult }
    }

    Invoke-SmokeStep 'section-cleanup-failure' {
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionCleanup')
        $page = Wait-ByAutomationId $window 'CleanupPage'
        $report = Wait-ByAutomationId $page 'CleanupReport'
        $initialReport = Get-ElementText $report
        $before = Get-DurableHashes $newRoot
        Invoke-Element (Wait-ByAutomationId $page 'CleanupConfirmButton')
        Start-Sleep -Milliseconds 300
        Assert-TreeEqual $before (Get-DurableHashes $newRoot) 'Cleanup preview validation'
        [ordered]@{ page = 'CleanupPage'; report = $initialReport; invoked = 'CleanupConfirmButton'; validatedNoMutation = 'preview-required' }
    }

    Invoke-SmokeStep 'section-dashboard-return' {
        Invoke-Element (Wait-ByAutomationId $window 'ShellSectionDashboard')
        $page = Wait-ByAutomationId $window 'DashboardPage'
        $null = Wait-ByAutomationId $page 'DashboardStatus'
        [ordered]@{ navigation = 'ShellSectionDashboard'; page = $page.Current.AutomationId }
    }

    Invoke-SmokeStep 'graceful-shutdown' {
        $closed = $process.CloseMainWindow()
        if (-not $closed) { throw 'AAML main window did not accept graceful close.' }
        if (-not $process.WaitForExit(10000)) { throw 'AAML did not exit after graceful close.' }
        if ($process.ExitCode -ne 0) { throw "AAML graceful exit code was $($process.ExitCode)." }
        [ordered]@{ exitCode = $process.ExitCode }
    }

    $writtenFiles = @(Get-ChildItem -LiteralPath $localAppData -File -Recurse)
    foreach ($file in $writtenFiles) {
        if (-not $file.FullName.StartsWith($newRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Application data escaped the new root: $($file.FullName)"
        }
    }
    $artifactHashesAfter = Get-TreeHashes $artifact
    Assert-TreeEqual $artifactHashesBefore $artifactHashesAfter 'Staged artifact'
    $oldRootAfter = if (Test-Path -LiteralPath $oldRoot) { Get-TreeHashes $oldRoot } else { [ordered]@{} }
    Assert-TreeEqual $oldRootBefore $oldRootAfter 'Former application root'
    $result = 'passed'
}
catch {
    $failure = $_.Exception.ToString()
    if ($null -ne $window) {
        Export-AutomationTree $window (Join-Path $evidence 'automation-tree-failure.json')
        $null = Save-WindowScreenshot $window (Join-Path $evidence 'failure.png')
    }
    throw
}
finally {
    if ($receiptPath -and (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { Copy-Item -LiteralPath $receiptPath -Destination (Join-Path $evidence 'migration-receipt.json') -Force }
    if ($logPath -and (Test-Path -LiteralPath $logPath -PathType Leaf)) { Copy-Item -LiteralPath $logPath -Destination (Join-Path $evidence 'aaml.log') -Force }
    [ordered]@{
        schemaVersion = 2; result = $result; failure = $failure; startedAtUtc = $startedAt; completedAtUtc = [DateTimeOffset]::UtcNow
        executable = $executable; expectedTitle = $ExpectedTitle; processId = if ($null -ne $process) { $process.Id } else { $null }
        localAppData = $localAppData; temp = $temp; newRoot = $newRoot; oldRoot = $oldRoot; seededSettingsSha256 = $seedHash
        artifactHashesBefore = $artifactHashesBefore; artifactHashesAfter = $artifactHashesAfter; steps = $steps
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'desktop-smoke-evidence.json') -Encoding utf8
    $env:LOCALAPPDATA = $previousLocalAppData
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}

'Windows exact-staged-artifact UI Automation smoke passed.'
