param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Commit,
    [string]$ArtifactDirectory,
    [string]$NuGetPackagesDirectory,
    [string]$LicenseSourcesDirectory,
    [switch]$ValidateLicenseCatalogOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$licenseTextRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'license-texts'))
if (-not $LicenseSourcesDirectory) { $LicenseSourcesDirectory = Join-Path $PSScriptRoot 'license-sources' }
if (-not $ArtifactDirectory) { $ArtifactDirectory = $OutputDirectory }
if (-not $NuGetPackagesDirectory) { $NuGetPackagesDirectory = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' } }

function Get-PropertyValue {
    param([object]$Object, [string[]]$Names)
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) { return $property.Value }
    }
    return $null
}

function Get-RequiredStrings {
    param([object]$Object, [string[]]$Names, [string]$Description)
    $value = Get-PropertyValue $Object $Names
    $values = @($value)
    if ($null -eq $value -or $values.Count -eq 0 -or @($values | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "$Description must contain one or more non-empty strings."
    }
    return @($values)
}

function Resolve-LicenseFile {
    param([string]$RelativePath, [string]$Description)
    if ([System.IO.Path]::IsPathRooted($RelativePath)) { throw "$Description must be repository-relative: $RelativePath" }
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $prefix = $licenseTextRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "$Description is outside eng/license-texts: $RelativePath" }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Description does not exist: $RelativePath" }
    return $fullPath
}

function Test-LicenseMappingMatch {
    param([object]$Mapping, [object]$Package)
    if (-not ($Mapping.versions -contains $Package.version)) { return $false }
    if ($Mapping.names | Where-Object { $_ -ieq $Package.name }) { return $true }
    foreach ($pattern in $Mapping.patterns) {
        if ([System.Management.Automation.WildcardPattern]::Get($pattern, [System.Management.Automation.WildcardOptions]::IgnoreCase).IsMatch($Package.name)) { return $true }
    }
    return $false
}

function Read-LicenseCatalog {
    param([object[]]$RestoredPackages)
    if (-not (Test-Path -LiteralPath $LicenseSourcesDirectory -PathType Container)) { throw "License source directory does not exist: $LicenseSourcesDirectory" }
    $mappings = @()
    $catalogFiles = @(Get-ChildItem -LiteralPath $LicenseSourcesDirectory -Filter '*.json' -File -Recurse | Sort-Object FullName)
    if ($catalogFiles.Count -eq 0) { throw "License source directory contains no JSON fragments: $LicenseSourcesDirectory" }
    foreach ($catalogFile in $catalogFiles) {
        try { $fragment = Get-Content -LiteralPath $catalogFile.FullName -Raw | ConvertFrom-Json } catch { throw "Malformed license catalog JSON '$($catalogFile.FullName)': $($_.Exception.Message)" }
        $topLevelKeys = @('entries', 'sources', 'packages' | Where-Object { $null -ne $fragment.PSObject.Properties[$_] })
        if ($topLevelKeys.Count -ne 1) { throw "License catalog '$($catalogFile.Name)' must contain exactly one of entries, sources, or packages." }
        $entries = @($fragment.($topLevelKeys[0]))
        if ($entries.Count -eq 0) { throw "License catalog '$($catalogFile.Name)' has no mappings." }
        for ($index = 0; $index -lt $entries.Count; $index++) {
            $entry = $entries[$index]
            $location = "$($catalogFile.Name) mapping $($index + 1)"
            $selectorKeys = @('packageNames', 'ids', 'packagePatterns' | Where-Object { $null -ne $entry.PSObject.Properties[$_] })
            if ($selectorKeys.Count -ne 1) { throw "$location must contain exactly one package selector: packageNames, ids, or packagePatterns." }
            $versionKeys = @('version', 'versions' | Where-Object { $null -ne $entry.PSObject.Properties[$_] })
            if ($versionKeys.Count -ne 1) { throw "$location must contain exactly one version selector: version or versions." }
            $selectors = Get-RequiredStrings $entry @($selectorKeys[0]) "$location package selector"
            $versions = Get-RequiredStrings $entry @($versionKeys[0]) "$location version selector"
            $spdx = Get-PropertyValue $entry @('spdx')
            $repository = Get-PropertyValue $entry @('repository')
            if ($spdx -isnot [string] -or [string]::IsNullOrWhiteSpace($spdx)) { throw "$location has no SPDX expression." }
            if ($repository -isnot [string] -or -not [Uri]::IsWellFormedUriString($repository, [UriKind]::Absolute)) { throw "$location has no valid authoritative repository URL." }
            $localFiles = Get-RequiredStrings $entry @('localFiles') "$location localFiles"
            $resolvedFiles = @($localFiles | ForEach-Object { Resolve-LicenseFile $_ "$location local file" })
            $secondaryNotices = @()
            $secondaryValue = Get-PropertyValue $entry @('secondaryNotices')
            foreach ($secondary in @($secondaryValue)) {
                if ($null -eq $secondary) { continue }
                $secondaryFile = Get-PropertyValue $secondary @('localFile')
                if ($secondaryFile -isnot [string] -or [string]::IsNullOrWhiteSpace($secondaryFile)) { throw "$location has a secondary notice without localFile." }
                $secondaryRepository = Get-PropertyValue $secondary @('repository')
                if ($secondaryRepository -isnot [string] -or -not [Uri]::IsWellFormedUriString($secondaryRepository, [UriKind]::Absolute)) { throw "$location has a secondary notice without a valid repository URL." }
                $secondaryNotices += [pscustomobject]@{ entry = $secondary; file = (Resolve-LicenseFile $secondaryFile "$location secondary notice") }
            }
            $mappings += [pscustomobject]@{
                location = $location
                names = if ($selectorKeys[0] -eq 'packagePatterns') { @() } else { $selectors }
                patterns = if ($selectorKeys[0] -eq 'packagePatterns') { $selectors } else { @() }
                versions = $versions
                spdx = $spdx
                repository = $repository
                ref = Get-PropertyValue $entry @('sourceRef', 'ref')
                commit = Get-PropertyValue $entry @('sourceCommit', 'commit')
                sourcePath = Get-PropertyValue $entry @('sourcePath', 'path')
                additionalSources = @(Get-PropertyValue $entry @('additionalSources'))
                notes = Get-PropertyValue $entry @('evidenceNotes', 'notes')
                files = $resolvedFiles
                secondaryNotices = $secondaryNotices
            }
        }
    }
    foreach ($mapping in $mappings) {
        if (@($RestoredPackages | Where-Object { Test-LicenseMappingMatch $mapping $_ }).Count -eq 0) { throw "Orphan license catalog entry: $($mapping.location) matches no restored name+version package." }
    }
    foreach ($package in $RestoredPackages) {
        $matches = @($mappings | Where-Object { Test-LicenseMappingMatch $_ $package })
        if ($matches.Count -gt 1) { throw "Ambiguous license catalog mapping for $($package.name) $($package.version): $(($matches.location) -join ', ')" }
    }
    return @($mappings)
}
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-supply-chain-policy.json') -Raw | ConvertFrom-Json
$canonicalRepository = $policy.canonicalRepository.TrimEnd('/')
$repositoryUrl = if ($Repository -match '^[^/]+/[^/]+$') { "https://github.com/$Repository" } else { $Repository.TrimEnd('/') }
if ($repositoryUrl -ne $canonicalRepository) { throw "Repository '$Repository' is not the canonical repository '$canonicalRepository'." }
$repositoryCoordinates = $repositoryUrl.Substring('https://github.com/'.Length)

$lockPaths = @('src/AAML.Avalonia/packages.lock.json')
if ($Rid -eq 'linux-x64') {
    $lockPaths += 'tools/AAML.ProtonWrapper/packages.lock.json'
    $lockPaths += 'tools/AAML.SteamProbe/packages.lock.json'
}
$firstPartyNames = @('AAML.Avalonia', 'AAML.Domain', 'AAML.Application', 'AAML.Infrastructure.Common', 'AAML.Infrastructure.Windows', 'AAML.Infrastructure.Linux', 'AAML.Infrastructure.Steam')
if ($Rid -eq 'linux-x64') { $firstPartyNames += @('AAML.ProtonWrapper', 'AAML.SteamProbe') }

$restoredPackages = @{}
$dependencyEdges = @{}
foreach ($relativeLockPath in $lockPaths) {
    $lock = Get-Content -LiteralPath (Join-Path $root $relativeLockPath) -Raw | ConvertFrom-Json
    foreach ($targetProperty in $lock.dependencies.PSObject.Properties) {
        if ($targetProperty.Name -ne 'net10.0' -and $targetProperty.Name -ne "net10.0/$Rid") { continue }
        foreach ($packageProperty in $targetProperty.Value.PSObject.Properties) {
            $entry = $packageProperty.Value
            if ($entry.type -eq 'Project') { continue }
            $key = "$($packageProperty.Name.ToLowerInvariant())@$($entry.resolved)"
            $restoredPackages[$key] = [ordered]@{ name = $packageProperty.Name; version = $entry.resolved; contentHash = $entry.contentHash }
            if (-not $dependencyEdges.ContainsKey($key)) { $dependencyEdges[$key] = @() }
            $entryDependencies = $entry.PSObject.Properties['dependencies']
            if ($null -ne $entryDependencies) {
                foreach ($dependency in $entryDependencies.Value.PSObject.Properties) {
                    $dependencyEntry = @($targetProperty.Value.PSObject.Properties | Where-Object { $_.Name -ieq $dependency.Name })
                    if ($dependencyEntry.Count -gt 0) {
                        $resolved = $dependencyEntry[0].Value.resolved
                        if ($resolved) { $dependencyEdges[$key] += "pkg:nuget/$($dependency.Name)@$resolved" }
                    }
                }
            }
        }
    }
}

# Runtime packs are represented only in publish deps, not project lock files. Keep their
# catalog entries fail-closed by admitting only installed Microsoft.NETCore.App versions.
$runtimeVersions = @(dotnet --list-runtimes | ForEach-Object { if ($_ -match '^Microsoft\.NETCore\.App\s+([^\s]+)\s+') { $Matches[1] } } | Sort-Object -Unique)
foreach ($runtimeVersion in $runtimeVersions) {
    foreach ($runtimeRid in @('win-x64', 'linux-x64')) {
        $runtimePackName = "runtimepack.Microsoft.NETCore.App.Runtime.$runtimeRid"
        $restoredPackages["$($runtimePackName.ToLowerInvariant())@$runtimeVersion"] = [ordered]@{ name = $runtimePackName; version = $runtimeVersion; contentHash = '' }
    }
}

$licenseMappings = Read-LicenseCatalog @($restoredPackages.Values)
if ($ValidateLicenseCatalogOnly) {
    "Validated $($licenseMappings.Count) license catalog mappings against $($restoredPackages.Count) restored name+version packages for $Rid."
    return
}

if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) { throw "Artifact directory does not exist: $ArtifactDirectory" }

function Get-RelativeArtifactPath {
    param([string]$Path)
    return [System.IO.Path]::GetRelativePath($ArtifactDirectory, $Path).Replace('\', '/')
}

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-ShippedAsset {
    param([string]$ApplicationDirectory, [string]$AssetPath, [string]$Group)
    $normalized = $AssetPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $candidates = @((Join-Path $ApplicationDirectory $normalized))
    if ($Group -eq 'resources') {
        $parent = Split-Path -Parent $normalized
        if ($parent) { $candidates += Join-Path (Join-Path $ApplicationDirectory (Split-Path -Leaf $parent)) (Split-Path -Leaf $normalized) }
    }
    $candidates += Join-Path $ApplicationDirectory (Split-Path -Leaf $normalized)
    $matches = @($candidates | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($matches.Count -eq 0) { throw "Declared $Group asset is missing from the staged artifact: $AssetPath" }
    if ($matches.Count -gt 1) { throw "Declared $Group asset resolves to multiple staged files: $AssetPath -> $($matches -join ', ')" }
    return [System.IO.Path]::GetFullPath($matches[0])
}

$packages = @{}
$firstParty = @{}
$ownership = @{}
$depsDocuments = @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter '*.deps.json' -File -Recurse | Sort-Object FullName)
if ($depsDocuments.Count -eq 0) { throw 'The staged artifact contains no .deps.json files.' }
foreach ($depsFile in $depsDocuments) {
    try { $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json } catch { throw "Malformed deps document '$($depsFile.FullName)': $($_.Exception.Message)" }
    $depsRelative = Get-RelativeArtifactPath $depsFile.FullName
    $runtimeTarget = [string]$deps.runtimeTarget.name
    if ($runtimeTarget -notmatch "/$([regex]::Escape($Rid))$") { throw "Deps runtime target '$runtimeTarget' does not match staged RID '$Rid': $depsRelative" }
    $targetProperty = $deps.targets.PSObject.Properties[$runtimeTarget]
    if ($null -eq $targetProperty) { throw "Deps document has no active runtime target '$runtimeTarget': $depsRelative" }
    $applicationDirectory = Split-Path -Parent $depsFile.FullName
    $depsHash = Get-FileSha256 $depsFile.FullName
    foreach ($libraryProperty in $targetProperty.Value.PSObject.Properties) {
        $identity = $libraryProperty.Name
        $slash = $identity.LastIndexOf('/')
        if ($slash -le 0) { throw "Invalid deps library identity '$identity' in $depsRelative" }
        $name = $identity.Substring(0, $slash)
        $libraryVersion = $identity.Substring($slash + 1)
        $libraryMetadata = $deps.libraries.PSObject.Properties[$identity]
        if ($null -eq $libraryMetadata) { throw "Deps target library '$identity' has no libraries record in $depsRelative" }
        $kind = [string]$libraryMetadata.Value.type
        if ($kind -notin @('package', 'project', 'runtimepack')) { continue }
        $activeAssets = @()
        foreach ($group in @('runtime', 'native', 'resources', 'runtimeTargets')) {
            $assetProperty = $libraryProperty.Value.PSObject.Properties[$group]
            if ($null -eq $assetProperty) { continue }
            foreach ($asset in $assetProperty.Value.PSObject.Properties) {
                if ($asset.Name -match '\.(?:pdb|mdb|dbg|dSYM)$') { continue }
                if ($group -eq 'runtimeTargets') {
                    $assetRid = [string]$asset.Value.rid
                    if ($assetRid -and $assetRid -ne $Rid) { continue }
                    $assetType = [string]$asset.Value.assetType
                    if ($assetType -and $assetType -notin @('runtime', 'native', 'resources')) { continue }
                }
                $activeAssets += [pscustomobject]@{ group = $group; asset = $asset }
            }
        }
        if ($activeAssets.Count -eq 0) { continue }
        $componentKey = "$($kind.ToLowerInvariant()):$($name.ToLowerInvariant())@$libraryVersion"
        if ($kind -in @('package', 'runtimepack')) {
            $nameKey = $name.ToLowerInvariant()
            $conflict = @($packages.Values | Where-Object { $_.name.ToLowerInvariant() -eq $nameKey -and $_.version -ne $libraryVersion })
            if ($conflict.Count -gt 0) { throw "Conflicting package versions ship for ${name}: $($conflict[0].version) and $libraryVersion" }
            if (-not $packages.ContainsKey($componentKey)) {
                $packages[$componentKey] = [ordered]@{ name = $name; version = $libraryVersion; kind = $kind; contentHash = [string]$libraryMetadata.Value.sha512; assets = @(); sourceDeps = @() }
            }
            $component = $packages[$componentKey]
            if ($kind -eq 'package') {
                $packageDirectory = Join-Path (Join-Path $NuGetPackagesDirectory $name.ToLowerInvariant()) $libraryVersion
                $packageHashPath = Join-Path $packageDirectory "$($name.ToLowerInvariant()).$libraryVersion.nupkg.sha512"
                if (-not (Test-Path -LiteralPath $packageHashPath -PathType Leaf)) { throw "NuGet package hash is unavailable for shipped package $name ${libraryVersion}: $packageHashPath" }
                $restoredKey = "$($name.ToLowerInvariant())@$libraryVersion"
                $expectedContentHash = if ($restoredPackages.ContainsKey($restoredKey)) { [string]$restoredPackages[$restoredKey].contentHash } else { (Get-Content -LiteralPath $packageHashPath -Raw).Trim() }
                $depsContentHash = ([string]$component.contentHash) -replace '^sha512-', ''
                if ($depsContentHash -and $expectedContentHash -ne $depsContentHash) { throw "Deps content hash is tampered for $name $libraryVersion in $depsRelative" }
            }
        } else {
            if (-not $firstParty.ContainsKey($componentKey)) { $firstParty[$componentKey] = [ordered]@{ name = $name; version = $Version; assets = @(); sourceDeps = @() } }
            $component = $firstParty[$componentKey]
        }
        $component.sourceDeps += "$depsRelative|$depsHash"
        foreach ($activeAsset in $activeAssets) {
                $group = $activeAsset.group
                $asset = $activeAsset.asset
                $shippedPath = Resolve-ShippedAsset $applicationDirectory $asset.Name $group
                $relative = Get-RelativeArtifactPath $shippedPath
                $assetHash = Get-FileSha256 $shippedPath
                if ($kind -eq 'package') {
                    $sourcePath = Join-Path $packageDirectory $asset.Name.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
                    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Declared package asset is unavailable in the restored package: $identity/$($asset.Name)" }
                    if ((Get-FileSha256 $sourcePath) -ne $assetHash) { throw "Staged package asset differs from restored package content: $relative ($identity)" }
                }
                $owner = $componentKey
                if ($ownership.ContainsKey($relative.ToLowerInvariant()) -and $ownership[$relative.ToLowerInvariant()] -ne $owner) { throw "Staged binary is multiply attributed: $relative" }
                $ownership[$relative.ToLowerInvariant()] = $owner
                $component.assets += "$relative|$assetHash"
        }
    }
    $appBase = $depsFile.Name.Substring(0, $depsFile.Name.Length - '.deps.json'.Length)
    foreach ($hostName in @($appBase, "$appBase.exe")) {
        $hostPath = Join-Path $applicationDirectory $hostName
        if (Test-Path -LiteralPath $hostPath -PathType Leaf) {
            $project = @($firstParty.Values | Where-Object { $_.name -eq $appBase })
            if ($project.Count -ne 1) { throw "Cannot attribute first-party app host: $(Get-RelativeArtifactPath $hostPath)" }
            $relative = Get-RelativeArtifactPath $hostPath
            $project[0].assets += "$relative|$(Get-FileSha256 $hostPath)"
            $ownership[$relative.ToLowerInvariant()] = "first-party:$($appBase.ToLowerInvariant())"
        }
    }
}

$steamManifestPath = Join-Path $ArtifactDirectory 'steamworks-manifest.json'
if (-not (Test-Path -LiteralPath $steamManifestPath -PathType Leaf)) { throw 'The staged Steamworks manifest is missing.' }
$steamManifest = Get-Content -LiteralPath $steamManifestPath -Raw | ConvertFrom-Json
$native = $steamManifest.nativeAssets.$Rid
$valvePath = Join-Path $ArtifactDirectory $native.file
if (-not (Test-Path -LiteralPath $valvePath -PathType Leaf)) { throw "Pinned Steam native asset is missing: $($native.file)" }
$valveAssets = @()
foreach ($candidate in @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter $native.file -File -Recurse)) {
    $candidateHash = Get-FileSha256 $candidate.FullName
    if ($candidateHash -ne ([string]$native.sha256).ToLowerInvariant() -or $candidate.Length -ne [long]$native.size) { throw "Pinned Steam native asset hash or size mismatch: $(Get-RelativeArtifactPath $candidate.FullName)" }
    $valveRelative = Get-RelativeArtifactPath $candidate.FullName
    if ($ownership.ContainsKey($valveRelative.ToLowerInvariant())) { throw "Valve native asset is multiply attributed: $valveRelative" }
    $ownership[$valveRelative.ToLowerInvariant()] = 'explicit:valve'
    $valveAssets += "$valveRelative|$candidateHash"
}

$binaryFiles = @(Get-ChildItem -LiteralPath $ArtifactDirectory -File -Recurse | Where-Object {
    $_.Name -match '\.(?:dll|exe|so)$' -or ($_.Extension -eq '' -and $_.Length -ge 4 -and [Convert]::ToHexString([System.IO.File]::ReadAllBytes($_.FullName)[0..3]) -eq '7F454C46')
})
foreach ($binary in $binaryFiles) {
    $relative = Get-RelativeArtifactPath $binary.FullName
    if (-not $ownership.ContainsKey($relative.ToLowerInvariant())) { throw "Shipped binary is unattributed: $relative" }
}

$emptyPackageKeys = @($packages.Keys | Where-Object { $packages[$_].assets.Count -eq 0 })
foreach ($key in $emptyPackageKeys) { $packages.Remove($key) }
$dependencyEdges = @{}
foreach ($package in $packages.Values) { $dependencyEdges["$($package.name.ToLowerInvariant())@$($package.version)"] = @() }
$components = @()
$nugetRoot = $NuGetPackagesDirectory
$noticeSections = @()
$mappedEvidenceFiles = @{}
$licenseGaps = @()
foreach ($package in @($packages.Values | Sort-Object name, version)) {
    $packageDirectory = Join-Path (Join-Path $nugetRoot $package.name.ToLowerInvariant()) $package.version
    $nuspecPath = Join-Path $packageDirectory "$($package.name.ToLowerInvariant()).nuspec"
    $licenseEvidence = 'not declared in the available package metadata'
    $licenseText = $null
    $licenses = @()
    if (Test-Path -LiteralPath $nuspecPath -PathType Leaf) {
        [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
        $license = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        if ($null -ne $license) {
            $licenseType = $license.type
            $licenseValue = $license.InnerText.Trim()
            $licenseEvidence = "$licenseType`: $licenseValue"
            if ($licenseType -eq 'expression') { $licenses += [ordered]@{ expression = $licenseValue } }
            if ($licenseType -eq 'file') {
                $candidate = Join-Path $packageDirectory $licenseValue
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $licenseText = Get-Content -LiteralPath $candidate -Raw }
            }
        } else {
            $licenseUrl = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='licenseUrl']")
            if ($null -ne $licenseUrl) {
                $licenseEvidence = "url: $($licenseUrl.InnerText)"
                $licenses += [ordered]@{ license = [ordered]@{ url = $licenseUrl.InnerText } }
            }
        }
    }
    if ($package.name -ieq 'Avalonia.Angle.Windows.Natives' -and $licenseText) {
        $licenseEvidence = 'file: LICENSE; SPDX mapping: BSD-3-Clause; source: https://github.com/AvaloniaUI/angle'
        $licenses = @([ordered]@{ expression = 'BSD-3-Clause' })
    }
    if ($package.name -ieq 'morelinq' -and $licenseText) {
        $licenseEvidence = 'file: COPYING.txt; SPDX mapping: Apache-2.0; source: https://github.com/morelinq/MoreLINQ'
        $licenses = @([ordered]@{ expression = 'Apache-2.0' })
    }
    $mapping = @($licenseMappings | Where-Object { Test-LicenseMappingMatch $_ $package })
    if ($mapping.Count -eq 1) {
        $mapping = $mapping[0]
        $licenses = @([ordered]@{ expression = $mapping.spdx })
        $sourceParts = @("repository: $($mapping.repository)")
        if ($mapping.ref) { $sourceParts += "ref: $($mapping.ref)" }
        if ($mapping.commit) { $sourceParts += "commit: $($mapping.commit)" }
        if ($mapping.sourcePath) { $sourceParts += "path: $($mapping.sourcePath)" }
        $licenseEvidence = "catalog SPDX: $($mapping.spdx); " + ($sourceParts -join '; ')
        foreach ($file in @($mapping.files)) { $mappedEvidenceFiles[$file.ToLowerInvariant()] = $file }
        foreach ($secondary in @($mapping.secondaryNotices)) { $mappedEvidenceFiles[$secondary.file.ToLowerInvariant()] = $secondary.file }
    }
    if (-not $licenseText -and $mapping.Count -eq 0) { $licenseGaps += "$($package.name) $($package.version): $licenseEvidence; full authoritative license text is unavailable locally" }
    $packageRef = if ($package.kind -eq 'runtimepack') { "pkg:generic/$($package.name)@$($package.version)" } else { "pkg:nuget/$($package.name)@$($package.version)" }
    $componentProperties = @()
    if ($package.contentHash) { $componentProperties += [ordered]@{ name = 'aaml:nuget-content-hash'; value = $package.contentHash } }
    $componentProperties += [ordered]@{ name = 'aaml:classification'; value = if ($package.kind -eq 'runtimepack') { 'runtime-pack' } else { 'nuget' } }
    $componentProperties += @($package.assets | Sort-Object -Unique | ForEach-Object { [ordered]@{ name = 'aaml:shipped-asset'; value = $_ } })
    $componentProperties += @($package.sourceDeps | Sort-Object -Unique | ForEach-Object { [ordered]@{ name = 'aaml:source-deps'; value = $_ } })
    $component = [ordered]@{
        type = 'library'
        'bom-ref' = $packageRef
        group = ''
        name = $package.name
        version = $package.version
        purl = $packageRef
        properties = $componentProperties
    }
    if ($licenses.Count -gt 0) { $component.licenses = $licenses }
    $components += $component
    if ($mapping.Count -eq 1) {
        $mappingFileNames = @($mapping.files | ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_).Replace('\', '/') })
        $mappingSources = @("Authoritative source: $($mapping.repository)")
        if ($mapping.ref) { $mappingSources += "Source ref: $($mapping.ref)" }
        if ($mapping.commit) { $mappingSources += "Source commit: $($mapping.commit)" }
        if ($mapping.sourcePath) { $mappingSources += "Source path: $($mapping.sourcePath)" }
        foreach ($source in @($mapping.additionalSources)) {
            if ($null -ne $source) { $mappingSources += "Additional source: $($source.repository); ref: $($source.sourceRef); path: $($source.sourcePath)" }
        }
        foreach ($secondary in @($mapping.secondaryNotices)) {
            $secondaryEntry = $secondary.entry
            $mappingSources += "Secondary notice: $($secondaryEntry.name); SPDX: $($secondaryEntry.spdx); source: $($secondaryEntry.repository); ref: $($secondaryEntry.ref); path: $($secondaryEntry.path)"
            if ($secondaryEntry.notes) { $mappingSources += "Secondary notice notes: $($secondaryEntry.notes)" }
        }
        if ($mapping.notes) { $mappingSources += "Mapping notes: $($mapping.notes)" }
        $noticeSections += "--- $($package.name) $($package.version) ---`nMapped SPDX: $($mapping.spdx)`n$($mappingSources -join "`n")`nAuthoritative local evidence: $($mappingFileNames -join ', ')`nFull text is included once in the authoritative evidence appendix."
    } else {
        $body = if ($licenseText) { $licenseText.Trim() } else { '[RELEASE-BLOCKING LICENSE TEXT GAP] Full license text was not present in the restored NuGet package and has no authoritative local catalog mapping.' }
        $noticeSections += "--- $($package.name) $($package.version) ---`nDeclared evidence: $licenseEvidence`n$body"
    }
}

foreach ($project in @($firstParty.Values | Sort-Object name)) {
    if ($project.assets.Count -eq 0) { continue }
    $properties = @([ordered]@{ name = 'aaml:classification'; value = 'first-party' })
    $properties += @($project.assets | Sort-Object -Unique | ForEach-Object { [ordered]@{ name = 'aaml:shipped-asset'; value = $_ } })
    $properties += @($project.sourceDeps | Sort-Object -Unique | ForEach-Object { [ordered]@{ name = 'aaml:source-deps'; value = $_ } })
    $components += [ordered]@{ type = 'application'; 'bom-ref' = "pkg:generic/$($project.name)@$Version"; name = $project.name; version = $Version; licenses = @([ordered]@{ license = [ordered]@{ id = 'GPL-3.0-only' } }); properties = $properties }
}
$steamLicensePath = Join-Path $ArtifactDirectory 'licenses/Steamworks.NET-LICENSE.txt'
if (-not (Test-Path -LiteralPath $steamLicensePath -PathType Leaf)) { throw 'The staged Steamworks.NET license is missing.' }
$steamLicenseEvidence = "$(Get-RelativeArtifactPath $steamLicensePath)|$(Get-FileSha256 $steamLicensePath)"
$steamDepsEvidence = "$(Get-RelativeArtifactPath $steamManifestPath)|$(Get-FileSha256 $steamManifestPath)"
$components += [ordered]@{ type = 'library'; 'bom-ref' = "pkg:generic/Steamworks.NET@$($steamManifest.steamworksNetCommit)"; name = 'Steamworks.NET'; version = $steamManifest.steamworksNetCommit; licenses = @([ordered]@{ license = [ordered]@{ id = 'MIT' } }); properties = @([ordered]@{ name = 'aaml:classification'; value = 'vendored-source' }, [ordered]@{ name = 'aaml:shipped-asset'; value = $steamLicenseEvidence }, [ordered]@{ name = 'aaml:source-deps'; value = $steamDepsEvidence }) }
$valveProperties = @([ordered]@{ name = 'aaml:classification'; value = 'steam-native-redistributable' }, [ordered]@{ name = 'aaml:redistribution-terms'; value = 'Steamworks SDK agreement' }, [ordered]@{ name = 'aaml:source-deps'; value = $steamDepsEvidence })
$valveProperties += @($valveAssets | Sort-Object -Unique | ForEach-Object { [ordered]@{ name = 'aaml:shipped-asset'; value = $_ } })
$components += [ordered]@{ type = 'library'; 'bom-ref' = "pkg:generic/Valve-Steamworks-SDK@$($steamManifest.steamworksSdkVersion)?rid=${Rid}"; name = $native.file; version = $steamManifest.steamworksSdkVersion; hashes = @([ordered]@{ alg = 'SHA-256'; content = $native.sha256 }); properties = $valveProperties }

$dependencies = @($dependencyEdges.Keys | Sort-Object | ForEach-Object { [ordered]@{ ref = "pkg:nuget/$($_.Split('@')[0])@$($_.Split('@')[1])"; dependsOn = @($dependencyEdges[$_] | Sort-Object -Unique) } })
$serialSeed = [Text.Encoding]::UTF8.GetBytes("$Rid|$Version|$Commit|$repositoryUrl")
$serialHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($serialSeed)).ToLowerInvariant()
$sbom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.6'; serialNumber = "urn:uuid:$($serialHash.Substring(0,8))-$($serialHash.Substring(8,4))-$($serialHash.Substring(12,4))-$($serialHash.Substring(16,4))-$($serialHash.Substring(20,12))"; version = 1
    metadata = [ordered]@{ component = [ordered]@{ type = 'application'; 'bom-ref' = "pkg:github/${repositoryCoordinates}@${Version}?rid=${Rid}"; name = 'Avalonia Alternative Mod Launcher'; version = $Version; purl = "pkg:github/${repositoryCoordinates}@$Version"; properties = @([ordered]@{ name = 'aaml:rid'; value = $Rid }, [ordered]@{ name = 'aaml:repository'; value = $repositoryUrl }, [ordered]@{ name = 'aaml:commit'; value = $Commit }) } }
    components = @($components | Sort-Object name, version)
    dependencies = $dependencies
    properties = @([ordered]@{ name = 'aaml:license-text-complete'; value = ($licenseGaps.Count -eq 0).ToString().ToLowerInvariant() }, [ordered]@{ name = 'aaml:release-blocking-license-gap-count'; value = [string]$licenseGaps.Count })
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sbom | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory $policy.sbom.fileName) -Encoding utf8
$gapSummary = if ($licenseGaps.Count -eq 0) { 'License text evidence is complete.' } else { "PUBLIC RELEASE BLOCKED: $($licenseGaps.Count) dependency license text gap(s) remain.`n`n" + ($licenseGaps -join "`n") }
$evidenceSections = @($mappedEvidenceFiles.Values | Sort-Object | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($root, $_).Replace('\', '/')
    "--- Authoritative local evidence: $relative ---`n$((Get-Content -LiteralPath $_ -Raw).Trim())"
})
@("AAML THIRD-PARTY NOTICES", "Repository: $repositoryUrl", "Version: $Version", "Commit: $Commit", "RID: $Rid", '', $gapSummary, '', ($noticeSections -join "`n`n"), '', '=== AUTHORITATIVE LOCAL EVIDENCE APPENDIX ===', ($evidenceSections -join "`n`n"), '', '--- Steamworks.NET vendored source ---', (Get-Content -LiteralPath (Join-Path $root 'src/ThirdParty/Steamworks.NET/LICENSE.txt') -Raw).Trim(), '', '--- Valve Steamworks native redistributable ---', $steamManifest.redistribution) | Set-Content -LiteralPath (Join-Path $OutputDirectory $policy.thirdPartyNotices.fileName) -Encoding utf8
"Generated CycloneDX 1.6 SBOM with $($components.Count) components and notices with $($licenseGaps.Count) release-blocking license gap(s) for $Rid."
