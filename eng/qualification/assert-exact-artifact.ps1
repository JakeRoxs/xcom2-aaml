param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedArchiveSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedRepository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ExtractionDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw "Archive does not exist: $ArchivePath" }
$actualArchiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -cne $ExpectedArchiveSha256.ToLowerInvariant()) { throw "Archive SHA-256 mismatch. Expected $ExpectedArchiveSha256, got $actualArchiveHash." }
if (Test-Path -LiteralPath $ExtractionDirectory) { Remove-Item -LiteralPath $ExtractionDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $ExtractionDirectory -Force | Out-Null

if ($ArchivePath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractionDirectory
} elseif ($ArchivePath.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase)) {
    tar -xzf $ArchivePath -C $ExtractionDirectory
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract archive: $ArchivePath" }
} else {
    throw 'Qualification accepts only .zip and .tar.gz archives.'
}

$metadataFiles = @(Get-ChildItem -LiteralPath $ExtractionDirectory -Filter 'release-metadata.json' -File -Recurse)
if ($metadataFiles.Count -ne 1) { throw "Archive must contain exactly one release-metadata.json; found $($metadataFiles.Count)." }
$artifactDirectory = Split-Path -Parent $metadataFiles[0].FullName
$metadata = Get-Content -LiteralPath $metadataFiles[0].FullName -Raw | ConvertFrom-Json
$normalizedExpectedRepository = $ExpectedRepository.TrimEnd('/')
$normalizedRepository = if ($metadata.repository -match '^[^/]+/[^/]+$') { "https://github.com/$($metadata.repository)" } else { $metadata.repository.TrimEnd('/') }
if ($normalizedRepository -cne $normalizedExpectedRepository) { throw "Embedded repository mismatch: $normalizedRepository" }
if ($metadata.commit -cne $ExpectedCommit) { throw "Embedded commit mismatch: $($metadata.commit)" }
if ($metadata.version -cne $ExpectedVersion) { throw "Embedded version mismatch: $($metadata.version)" }
if ($metadata.rid -notin @('win-x64', 'linux-x64')) { throw "Unsupported embedded RID: $($metadata.rid)" }

$checksumPath = Join-Path $artifactDirectory 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'Archive is missing SHA256SUMS.' }
$expectedFiles = @(Get-ChildItem -LiteralPath $artifactDirectory -File -Recurse | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object { [IO.Path]::GetRelativePath($artifactDirectory, $_.FullName).Replace('\', '/') } | Sort-Object)
$listedFiles = @()
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Invalid SHA256SUMS line: $line" }
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)' -or $listedFiles -contains $relative) { throw "Unsafe or duplicate SHA256SUMS path: $relative" }
    $file = Join-Path $artifactDirectory $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "SHA256SUMS file is missing: $relative" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $Matches[1].ToUpperInvariant()) { throw "SHA256SUMS mismatch: $relative" }
    $listedFiles += $relative
}
if (@(Compare-Object $expectedFiles ($listedFiles | Sort-Object)).Count -ne 0) { throw 'SHA256SUMS does not cover exactly the extracted files.' }

[ordered]@{
    archiveSha256 = $actualArchiveHash
    artifactDirectory = $artifactDirectory
    rid = $metadata.rid
    repository = $normalizedRepository
    commit = $metadata.commit
    version = $metadata.version
}
