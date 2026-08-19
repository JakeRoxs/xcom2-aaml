param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$validator = Join-Path $PSScriptRoot 'validate-aaml-artifact.ps1'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "aaml-package-policy-$([guid]::NewGuid().ToString('N'))"

function Assert-ObsoleteTopologyRejected([string]$RelativePath, [bool]$Directory) {
    $fixture = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $ArtifactDirectory -Destination $fixture -Recurse
    $obsoletePath = Join-Path $fixture $RelativePath
    if ($Directory) {
        New-Item -ItemType Directory -Path $obsoletePath -Force | Out-Null
    }
    else {
        $parent = Split-Path -Parent $obsoletePath
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [System.IO.File]::WriteAllText($obsoletePath, 'obsolete')
    }

    $rejected = $false
    try {
        & $validator -ArtifactDirectory $fixture -ManifestPath $ManifestPath
    }
    catch {
        if ($_.Exception.Message -notmatch 'Forbidden artifact topology is present') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Obsolete package topology was accepted: $RelativePath" }
}

try {
    & $validator -ArtifactDirectory $ArtifactDirectory -ManifestPath $ManifestPath -VerifyChecksums
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Assert-ObsoleteTopologyRejected 'tools/steam-probe' $true
    Assert-ObsoleteTopologyRejected 'tools/steam-probe/AAML.SteamProbe' $false
    Assert-ObsoleteTopologyRejected 'AAML.SteamProbe' $false
    'Validated consolidated package topology and rejected obsolete Steam probe payloads.'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
