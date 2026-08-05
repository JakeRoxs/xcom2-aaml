param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$ExtractionDirectory,
    [switch]$OfficialRelease
)

$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $ExtractionDirectory) {
    Remove-Item -LiteralPath $ExtractionDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $ExtractionDirectory -Force | Out-Null

if ($ArchivePath.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractionDirectory
} elseif ($ArchivePath.EndsWith('.tar.gz', [System.StringComparison]::OrdinalIgnoreCase)) {
    tar -xzf $ArchivePath -C $ExtractionDirectory
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract archive: $ArchivePath" }
} else {
    throw "Unsupported archive format: $ArchivePath"
}

$artifactDirectory = Join-Path $ExtractionDirectory $Rid
if (-not (Test-Path -LiteralPath $artifactDirectory -PathType Container)) {
    throw "Archive does not contain its expected root directory: $Rid"
}

& (Join-Path $PSScriptRoot 'validate-aaml-artifact.ps1') -ArtifactDirectory $artifactDirectory -ManifestPath $ManifestPath -VerifyChecksums -OfficialRelease:$OfficialRelease
if (-not $?) { throw 'Extracted artifact validation failed.' }

$archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $(Split-Path -Leaf $ArchivePath)" | Set-Content -LiteralPath "$ArchivePath.sha256" -Encoding ascii
"Validated extracted $Rid archive and wrote $ArchivePath.sha256."
