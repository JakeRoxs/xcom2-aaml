param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [switch]$GenerateChecksums,
    [switch]$VerifyChecksums,
    [switch]$OfficialRelease
)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$metadataPath = Join-Path $ArtifactDirectory 'release-metadata.json'

function Get-CanonicalHash([string]$Path) {
    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    $textExtensions = @('.svg', '.json', '.xml', '.txt', '.md', '.yml', '.yaml', '.toml')
    $content = if ($textExtensions -contains $extension) {
        $text = [System.IO.File]::ReadAllText($Path)
        $text -replace "`r`n", "`n" -replace "`r", "`n"
    }
    else {
        [System.IO.File]::ReadAllBytes($Path)
    }

    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = if ($content -is [byte[]]) { $content } else { [System.Text.UTF8Encoding]::new($false).GetBytes($content) }
        return [System.BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally { $hasher.Dispose() }
}

if ($GenerateChecksums -and $VerifyChecksums) { throw 'GenerateChecksums and VerifyChecksums cannot be used together.' }
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) { throw "Artifact directory does not exist: $ArtifactDirectory" }

$items = @(Get-Item -LiteralPath $ArtifactDirectory -Force) + @(Get-ChildItem -LiteralPath $ArtifactDirectory -Force -Recurse)
foreach ($item in $items) {
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Artifact contains a symlink or reparse point: $($item.FullName)"
    }
}

foreach ($relative in $manifest.required) {
    if (-not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $relative))) {
        throw "Required artifact file is missing: $relative"
    }
}

$brandingDirectory = Join-Path $ArtifactDirectory 'branding'
$brandProvenance = Get-Content -LiteralPath (Join-Path $brandingDirectory 'provenance.json') -Raw | ConvertFrom-Json
$brandManifest = Get-Content -LiteralPath (Join-Path $brandingDirectory 'asset-manifest.json') -Raw | ConvertFrom-Json
$brandSource = Join-Path $brandingDirectory 'aaml-icon.svg'
if ($brandProvenance.repository -ne 'https://github.com/JakeRoxs/xcom2-aaml' -or
    $brandProvenance.canonicalSource -ne 'assets/branding/aaml-icon.svg' -or
    $brandProvenance.generator -ne 'eng/generate-brand-assets.ps1' -or
    $brandProvenance.license -ne 'GPL-3.0-only' -or
    $brandProvenance.declaration -notmatch 'without copying or tracing' -or
    @($brandProvenance.externalAssets).Count -ne 0 -or
    @($brandProvenance.legacyAssets).Count -ne 0) { throw 'Packaged brand provenance is not AAML-owned and self-contained.' }
if ((Get-CanonicalHash $brandSource) -ne $brandManifest.files.'aaml-icon.svg'.sha256) { throw 'Packaged scalable brand source differs from its generated asset manifest.' }
$brandSvg = Get-Content -LiteralPath $brandSource -Raw
if ($brandSvg -match '<(?:image|text|use)\b|(?:href|font-family|url)\s*=') { throw 'Packaged brand source contains non-geometric or external content.' }

$files = @(Get-ChildItem -LiteralPath $ArtifactDirectory -File -Force -Recurse)
$forbiddenBrandNames = @($files | Where-Object { $_.Extension -match '^\.(?:ico|png|svg|jpg|jpeg|webp)$' -and $_.Name -match '(?i)^(?:xcom|firaxis|wotc|legacy.?aml)' })
if ($forbiddenBrandNames.Count -ne 0) { throw "Forbidden legacy/game artwork is present: $($forbiddenBrandNames.Name -join ', ')" }
$leafNames = $files.Name
foreach ($native in $manifest.forbiddenNative) {
    if ($leafNames -contains $native) { throw "Forbidden native asset is present: $native" }
}
foreach ($prefix in $manifest.forbiddenManagedPrefixes) {
    if ($leafNames -match "^$([regex]::Escape($prefix))(?:\.|$)") {
        throw "Obsolete modern AML assembly is present: $prefix"
    }
}

$forbiddenPatterns = @(
    '^steam_appid\.txt$', '^settings\.json$', '^AMLSettings\.json$',
    '\.log$', '\.tmp$', '\.bak$', 'sentry', 'telemetry', 'crashlytics',
    '^steam_api\.dll$', '\.(?:cs|csproj|sln|slnx|user|pdb|mdb|dbg|dSYM)$',
    '^(?:xcom2-launcher|AML)(?:\.exe|\.dll)?$', '(?:^|/)(?:src|source|tests?|fixtures?|debug)(?:/|$)'
)
foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $file.FullName).Replace('\', '/')
    foreach ($pattern in $forbiddenPatterns) {
        if ($file.Name -match $pattern -or $relative -match $pattern) { throw "Forbidden artifact file is present: $relative" }
    }
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($metadata.rid -ne $manifest.rid) { throw "Metadata RID '$($metadata.rid)' does not match '$($manifest.rid)'." }
if ([string]::IsNullOrWhiteSpace($metadata.version) -or [string]::IsNullOrWhiteSpace($metadata.repository) -or [string]::IsNullOrWhiteSpace($metadata.commit)) {
    throw 'Release metadata is incomplete.'
}
if (-not $metadata.selfContained -or $metadata.singleFile -or $metadata.trimmed -or $metadata.nativeAot) {
    throw 'Initial package policy requires self-contained, multi-file, untrimmed, non-AOT output.'
}

$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-supply-chain-policy.json') -Raw | ConvertFrom-Json
$executable = Join-Path $ArtifactDirectory $manifest.executable
$canonicalRepository = $policy.canonicalRepository.TrimEnd('/')
$metadataRepository = if ($metadata.repository -match '^[^/]+/[^/]+$') { "https://github.com/$($metadata.repository)" } else { $metadata.repository.TrimEnd('/') }
if ($metadataRepository -ne $canonicalRepository) { throw "Metadata repository is not canonical: $metadataRepository" }
$sbom = Get-Content -LiteralPath (Join-Path $ArtifactDirectory $policy.sbom.fileName) -Raw | ConvertFrom-Json
if ($sbom.bomFormat -ne 'CycloneDX' -or $sbom.specVersion -ne '1.6' -or $sbom.version -lt 1 -or $sbom.components.Count -lt 1) { throw 'SBOM is not a minimally valid CycloneDX 1.6 document.' }
$sbomComponent = $sbom.metadata.component
if ($sbomComponent.version -ne $metadata.version) { throw 'SBOM version does not match release metadata.' }
$sbomProperties = @{}; foreach ($property in $sbomComponent.properties) { $sbomProperties[$property.name] = $property.value }
if ($sbomProperties['aaml:rid'] -ne $metadata.rid -or $sbomProperties['aaml:repository'] -ne $canonicalRepository -or $sbomProperties['aaml:commit'] -ne $metadata.commit) { throw 'SBOM RID/repository/commit identity does not match release metadata.' }
$sbomRootProperties = @{}; foreach ($property in $sbom.properties) { $sbomRootProperties[$property.name] = $property.value }
$assetOwners = @{}
$sourceDeps = @{}
foreach ($component in $sbom.components) {
    $componentAssets = @($component.properties | Where-Object name -eq 'aaml:shipped-asset')
    if ($componentAssets.Count -eq 0) { throw "SBOM component has no shipped asset evidence: $($component.'bom-ref')" }
    foreach ($property in $componentAssets) {
        if ($property.value -notmatch '^(.+)\|([0-9a-fA-F]{64})$') { throw "Invalid shipped asset evidence on $($component.'bom-ref'): $($property.value)" }
        $relative = $Matches[1]
        $expectedHash = $Matches[2]
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)') { throw "Unsafe SBOM shipped asset path: $relative" }
        $assetPath = Join-Path $ArtifactDirectory $relative
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "SBOM shipped asset is missing: $relative" }
        if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ne $expectedHash) { throw "SBOM shipped asset hash mismatch: $relative" }
        $key = $relative.ToLowerInvariant()
        if ($assetOwners.ContainsKey($key) -and $assetOwners[$key] -ne $component.'bom-ref') { throw "SBOM asset is multiply attributed: $relative" }
        $assetOwners[$key] = $component.'bom-ref'
    }
    foreach ($property in @($component.properties | Where-Object name -eq 'aaml:source-deps')) {
        if ($property.value -notmatch '^(.+)\|([0-9a-fA-F]{64})$') { throw "Invalid source deps evidence on $($component.'bom-ref'): $($property.value)" }
        $relative = $Matches[1]
        $expectedHash = $Matches[2]
        $depsPath = Join-Path $ArtifactDirectory $relative
        if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw "SBOM source deps is missing: $relative" }
        if ((Get-FileHash -LiteralPath $depsPath -Algorithm SHA256).Hash -ne $expectedHash) { throw "SBOM source deps hash mismatch: $relative" }
        $sourceDeps[$relative.ToLowerInvariant()] = $true
    }
}
foreach ($depsFile in @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter '*.deps.json' -File -Recurse)) {
    $relative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $depsFile.FullName).Replace('\', '/')
    if (-not $sourceDeps.ContainsKey($relative.ToLowerInvariant())) { throw "Shipped deps document has no SBOM provenance: $relative" }
}
foreach ($binary in @($files | Where-Object {
            $_.Name -match '\.(?:dll|exe|so)$' -or ($_.Extension -eq '' -and $_.Length -ge 4 -and [Convert]::ToHexString([System.IO.File]::ReadAllBytes($_.FullName)[0..3]) -eq '7F454C46')
        })) {
    $relative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $binary.FullName).Replace('\', '/')
    if (-not $assetOwners.ContainsKey($relative.ToLowerInvariant())) { throw "Shipped binary has no SBOM owner: $relative" }
}
$notices = Get-Content -LiteralPath (Join-Path $ArtifactDirectory $policy.thirdPartyNotices.fileName) -Raw
if ($notices -notmatch [regex]::Escape("Repository: $canonicalRepository") -or $notices -notmatch [regex]::Escape("Version: $($metadata.version)") -or $notices -notmatch [regex]::Escape("Commit: $($metadata.commit)")) { throw 'Third-party notices identity does not match release metadata.' }
if ($OfficialRelease) {
    if ($manifest.rid -eq 'win-x64' -and $policy.signing.windowsAuthenticode.requiredForPublicRelease -ne $false) { throw 'Official Windows release policy must explicitly permit unsigned executables.' }
    if ($manifest.rid -eq 'win-x64' -and $policy.signing.windowsAuthenticode.status -ne 'not-required') { throw 'Official Windows release policy must mark Authenticode as not-required.' }
    if ($policy.archiveAttestations.requiredForOfficialGitHubRelease -ne $true) { throw 'Official release policy must require archive attestations.' }
    if ($sbomRootProperties['aaml:license-text-complete'] -ne 'true') { throw "Official release is blocked by $($sbomRootProperties['aaml:release-blocking-license-gap-count']) license text completeness gap(s)." }
}

$magic = [System.IO.File]::ReadAllBytes($executable)[0..3]
$hex = [Convert]::ToHexString($magic)
if ($manifest.executableFormat -eq 'pe' -and -not $hex.StartsWith('4D5A')) { throw 'Windows executable is not PE format.' }
if ($manifest.executableFormat -eq 'elf' -and $hex -ne '7F454C46') { throw 'Linux executable is not ELF format.' }
if ($manifest.rid -eq 'linux-x64' -and -not $IsWindows) {
    $mode = [System.IO.File]::GetUnixFileMode($executable)
    if (($mode -band [System.IO.UnixFileMode]::UserExecute) -eq 0) { throw 'Linux executable permission is missing.' }
}

if ($manifest.rid -eq 'win-x64') {
    $icoPath = Join-Path $brandingDirectory 'aaml.ico'
    if ((Get-FileHash -LiteralPath $icoPath -Algorithm SHA256).Hash -ne $brandManifest.files.'aaml.ico'.sha256) { throw 'Packaged Windows ICO differs from its generated asset manifest.' }
    $icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
    $icoReader = [System.IO.BinaryReader]::new([System.IO.MemoryStream]::new($icoBytes))
    try {
        if ($icoReader.ReadUInt16() -ne 0 -or $icoReader.ReadUInt16() -ne 1 -or $icoReader.ReadUInt16() -ne 9) { throw 'Packaged Windows ICO directory is invalid.' }
        $frames = @()
        for ($index = 0; $index -lt 9; $index++) {
            $width = $icoReader.ReadByte(); $null = $icoReader.ReadBytes(15)
            $frames += if ($width -eq 0) { 256 } else { [int]$width }
        }
        if (Compare-Object @(16, 20, 24, 32, 40, 48, 64, 128, 256) $frames) { throw 'Packaged Windows ICO frame sizes are incomplete.' }
    }
    finally { $icoReader.Dispose() }
    if ($IsWindows) {
        Add-Type -AssemblyName System.Drawing
        $associatedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($executable)
        if ($null -eq $associatedIcon) { throw 'Windows executable has no extractable application icon metadata.' }
        $associatedIcon.Dispose()
    }
}
else {
    $desktopId = 'io.github.jakeroxs.xcom2_aaml'
    $desktop = Get-Content -LiteralPath (Join-Path $ArtifactDirectory "share/applications/$desktopId.desktop") -Raw
    [xml]$appstream = Get-Content -LiteralPath (Join-Path $ArtifactDirectory "share/metainfo/$desktopId.metainfo.xml") -Raw
    if ($desktop -notmatch "(?m)^Icon=$desktopId`$" -or $desktop -notmatch '(?m)^Exec=AAML\.Avalonia$') { throw 'Packaged Linux desktop references are invalid.' }
    if ($appstream.component.id -ne $desktopId -or $appstream.component.launchable.'#text' -ne "$desktopId.desktop") { throw 'Packaged AppStream references are invalid.' }
    foreach ($size in @(16, 32, 48, 64, 128, 256, 512)) {
        $iconPath = Join-Path $ArtifactDirectory "share/icons/hicolor/${size}x$size/apps/$desktopId.png"
        $expected = $brandManifest.files."png/aaml-$size.png".sha256
        if ((Get-FileHash -LiteralPath $iconPath -Algorithm SHA256).Hash -ne $expected) { throw "Packaged Linux $size px icon differs from its generated asset manifest." }
        $png = [System.IO.File]::ReadAllBytes($iconPath)
        $width = $png[16] * 16777216 + $png[17] * 65536 + $png[18] * 256 + $png[19]
        $height = $png[20] * 16777216 + $png[21] * 65536 + $png[22] * 256 + $png[23]
        if ($width -ne $size -or $height -ne $size) { throw "Packaged Linux icon has invalid dimensions: $iconPath" }
    }
    $scalable = Join-Path $ArtifactDirectory "share/icons/hicolor/scalable/apps/$desktopId.svg"
    if ((Get-CanonicalHash $scalable) -ne $brandManifest.files.'aaml-icon.svg'.sha256) { throw 'Packaged Linux scalable icon differs from canonical source.' }
}

$steamManifestPath = Join-Path $ArtifactDirectory 'steamworks-manifest.json'
$steamManifest = Get-Content -LiteralPath $steamManifestPath -Raw | ConvertFrom-Json
$nativeAsset = $steamManifest.nativeAssets.($manifest.rid)
if ($null -eq $nativeAsset) { throw "Steamworks manifest has no native asset for $($manifest.rid)." }
if ([System.IO.Path]::IsPathRooted($nativeAsset.file) -or $nativeAsset.file -ne [System.IO.Path]::GetFileName($nativeAsset.file)) {
    throw "Steamworks manifest contains an unsafe native asset path: $($nativeAsset.file)"
}
$nativePath = Join-Path $ArtifactDirectory $nativeAsset.file
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) { throw "Pinned Steam native asset is missing: $($nativeAsset.file)" }
$nativeFile = Get-Item -LiteralPath $nativePath
if ($nativeFile.Length -ne [long]$nativeAsset.size) { throw "Steam native asset size does not match steamworks-manifest.json: $($nativeAsset.file)" }
$nativeHash = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash
if ($nativeHash -ne $nativeAsset.sha256) { throw "Steam native asset hash does not match steamworks-manifest.json: $($nativeAsset.file)" }

if ($GenerateChecksums) {
    $checksumPath = Join-Path $ArtifactDirectory 'SHA256SUMS'
    $lines = $files | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($ArtifactDirectory, $_.FullName).Replace('\', '/')
        "$(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
    } | Sort-Object
    $lines | Set-Content -LiteralPath $checksumPath -Encoding ascii
}


if ($VerifyChecksums) {
    $checksumPath = Join-Path $ArtifactDirectory 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'SHA256SUMS is missing.' }
    $expectedFiles = @($files | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object {
            [System.IO.Path]::GetRelativePath($ArtifactDirectory, $_.FullName).Replace('\', '/')
        } | Sort-Object)
    $listedFiles = @()
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw "Invalid SHA256SUMS line: $line" }
        $expectedHash = $Matches[1]
        $relative = $Matches[2]
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)') { throw "Unsafe SHA256SUMS path: $relative" }
        if ($listedFiles -contains $relative) { throw "Duplicate SHA256SUMS path: $relative" }
        $listedFiles += $relative
        $path = Join-Path $ArtifactDirectory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "SHA256SUMS references a missing file: $relative" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash) { throw "Checksum mismatch: $relative" }
    }
    if (Compare-Object $expectedFiles ($listedFiles | Sort-Object)) { throw 'SHA256SUMS does not list exactly the packaged files.' }
}

"Validated $($manifest.rid) artifact with $($files.Count) files."
