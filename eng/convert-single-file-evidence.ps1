param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$SourceArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$BundlePath,
    [Parameter(Mandatory = $true)][string]$SourceDepsPath,
    [Parameter(Mandatory = $true)][string]$PackagedDepsPath,
    [Parameter(Mandatory = $true)][string]$SourceRuntimeConfigPath,
    [Parameter(Mandatory = $true)][string]$PackagedRuntimeConfigPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'dotnet-bundle.psm1') -Force
$bundle = Get-Item -LiteralPath $BundlePath
$bundleRelative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $bundle.FullName).Replace('\', '/')
$bundleHash = (Get-FileHash -LiteralPath $bundle.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceDepsHash = (Get-FileHash -LiteralPath $SourceDepsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$packagedDepsHash = (Get-FileHash -LiteralPath $PackagedDepsPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceDepsHash -ne $packagedDepsHash) { throw 'Packaged dependency evidence differs from the publish dependency graph.' }
$packagedDepsRelative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $PackagedDepsPath).Replace('\', '/')
$sourceRuntimeConfigHash = (Get-FileHash -LiteralPath $SourceRuntimeConfigPath -Algorithm SHA256).Hash.ToLowerInvariant()
$packagedRuntimeConfigHash = (Get-FileHash -LiteralPath $PackagedRuntimeConfigPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceRuntimeConfigHash -ne $packagedRuntimeConfigHash) { throw 'Packaged runtime configuration differs from the bundle runtime configuration.' }
$sourceRoot = [System.IO.Path]::GetFullPath($SourceArtifactDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$sourcePrefix = $sourceRoot + [System.IO.Path]::DirectorySeparatorChar
$bundleEntries = @{}
foreach ($entry in Get-DotNetBundleEntries -BundlePath $bundle.FullName) { $bundleEntries[$entry.Path] = $entry }
$bundleEntryHashes = @{}
foreach ($requiredGraphFile in @('AAML.Avalonia.deps.json', 'AAML.Avalonia.runtimeconfig.json')) {
    $sourceGraphPath = Join-Path $sourceRoot $requiredGraphFile
    if (-not (Test-Path -LiteralPath $sourceGraphPath -PathType Leaf) -or -not $bundleEntries.ContainsKey($requiredGraphFile)) { throw "Required deployment graph is missing from the evidence publish or bundle: $requiredGraphFile" }
    $sourceGraphHash = (Get-FileHash -LiteralPath $sourceGraphPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $bundleGraphHash = Get-DotNetBundleEntryHash -BundlePath $bundle.FullName -Entry $bundleEntries[$requiredGraphFile]
    if ($sourceGraphHash -ne $bundleGraphHash) { throw "Embedded deployment graph differs from the evidence publish: $requiredGraphFile" }
    $bundleEntryHashes[$requiredGraphFile] = $bundleGraphHash
}

$sbomPath = Join-Path $ArtifactDirectory 'sbom.cdx.json'
$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
$convertedComponents = [System.Collections.Generic.List[object]]::new()
foreach ($component in $sbom.components) {
    $properties = [System.Collections.Generic.List[object]]::new()
    $hasEmbeddedBundle = $false
    foreach ($property in @($component.properties)) {
        if ($property.name -eq 'aaml:source-deps') {
            if ($property.value -notmatch '^(.+)\|([0-9a-fA-F]{64})$') { throw "Invalid source dependency evidence: $($property.value)" }
            $properties.Add([ordered]@{ name = 'aaml:source-deps'; value = "$packagedDepsRelative|$packagedDepsHash" })
            continue
        }
        if ($property.name -ne 'aaml:shipped-asset') {
            $properties.Add($property)
            continue
        }
        if ($property.value -notmatch '^(.+)\|([0-9a-fA-F]{64})$') { throw "Invalid shipped asset evidence: $($property.value)" }
        $relative = $Matches[1]
        $hash = $Matches[2].ToLowerInvariant()
        $normalized = $relative.Replace('\', '/')
        if ([System.IO.Path]::IsPathRooted($relative) -or $normalized -match '(^|/)\.\.(/|$)') { throw "Unsafe shipped asset path: $relative" }
        $packagedPath = Join-Path $ArtifactDirectory $relative
        $isApplicationBundleAsset = $component.name -eq 'AAML.Avalonia' -and $relative -in @('AAML.Avalonia', 'AAML.Avalonia.exe', 'AAML.Avalonia.dll')
        if ($isApplicationBundleAsset -and $relative -ne 'AAML.Avalonia.dll') { continue }
        if (-not $isApplicationBundleAsset -and (Test-Path -LiteralPath $packagedPath -PathType Leaf) -and (Get-FileHash -LiteralPath $packagedPath -Algorithm SHA256).Hash -eq $hash) {
            $properties.Add($property)
            continue
        }
        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
        if (-not $sourcePath.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Embedded source asset is missing or outside the evidence publish: $relative" }
        $sourceFile = Get-Item -LiteralPath $sourcePath
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $hash) { throw "Embedded source asset hash differs from generated evidence: $relative" }
        if (-not $bundleEntries.ContainsKey($normalized)) { throw "Dependency graph asset is absent from the .NET bundle manifest: $relative" }
        $entry = $bundleEntries[$normalized]
        if ([long]$entry.Size -ne $sourceFile.Length) { throw "Embedded bundle entry size differs from the evidence publish: $relative" }
        if (-not $bundleEntryHashes.ContainsKey($normalized)) { $bundleEntryHashes[$normalized] = Get-DotNetBundleEntryHash -BundlePath $bundle.FullName -Entry $entry }
        if ($bundleEntryHashes[$normalized] -ne $hash) { throw "Embedded bundle entry hash differs from the evidence publish: $relative" }
        $properties.Add([ordered]@{ name = 'aaml:embedded-asset'; value = "$relative|$hash" })
        if (-not $hasEmbeddedBundle) {
            $properties.Add([ordered]@{ name = 'aaml:embedded-bundle'; value = "$bundleRelative|$bundleHash" })
            $hasEmbeddedBundle = $true
        }
    }
    if ($component.name -eq 'AAML.Avalonia') {
        $properties.Add([ordered]@{ name = 'aaml:shipped-asset'; value = "$bundleRelative|$bundleHash" })
    }
    $component.properties = @($properties)
    if (@($component.properties | Where-Object { $_.name -in @('aaml:shipped-asset', 'aaml:embedded-asset') }).Count -gt 0) { $convertedComponents.Add($component) }
}
$sbom.components = @($convertedComponents)
$componentRefs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($component in $sbom.components) { $null = $componentRefs.Add([string]$component.'bom-ref') }
$sbom.dependencies = @($sbom.dependencies | Where-Object { $componentRefs.Contains([string]$_.ref) } | ForEach-Object {
    $_.dependsOn = @($_.dependsOn | Where-Object { $componentRefs.Contains([string]$_) })
    $_
})
$sbom | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $sbomPath -Encoding utf8
