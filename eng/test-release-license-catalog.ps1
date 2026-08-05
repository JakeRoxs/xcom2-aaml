Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generator = Join-Path $PSScriptRoot 'generate-release-evidence.ps1'
$repository = 'JakeRoxs/xcom2-aaml'
$commit = '0000000000000000000000000000000000000000'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "aaml-license-catalog-$([guid]::NewGuid().ToString('N'))"

function Invoke-CatalogValidation {
    param([string]$CatalogDirectory)
    & $generator -Rid win-x64 -OutputDirectory (Join-Path $scratch 'output') -Version 0.0.0-test -Repository $repository -Commit $commit -LicenseSourcesDirectory $CatalogDirectory -ValidateLicenseCatalogOnly
}

function Assert-CatalogFailure {
    param([string]$Name, [string]$ExpectedMessage)
    $failed = $false
    try { Invoke-CatalogValidation (Join-Path $scratch $Name) | Out-Null } catch {
        $failed = $true
        if ($_.Exception.Message -notmatch $ExpectedMessage) { throw "Test '$Name' failed with an unexpected error: $($_.Exception.Message)" }
    }
    if (-not $failed) { throw "Test '$Name' expected catalog validation to fail." }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Invoke-CatalogValidation (Join-Path $PSScriptRoot 'license-sources') | Out-Null

    $malformed = Join-Path $scratch 'malformed'
    New-Item -ItemType Directory -Path $malformed | Out-Null
    '{ "entries": [{ "packageNames": ["Avalonia"], "version": "12.0.4" }] }' | Set-Content -LiteralPath (Join-Path $malformed 'catalog.json') -Encoding utf8
    Assert-CatalogFailure 'malformed' 'SPDX expression'

    $orphan = Join-Path $scratch 'orphan'
    New-Item -ItemType Directory -Path $orphan | Out-Null
    @{
        entries = @(@{ packageNames = @('Not.A.Restored.Package'); version = '1.0.0'; spdx = 'MIT'; repository = 'https://example.invalid/source'; localFiles = @('eng/license-texts/dotnet-foundation-mit.txt') })
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $orphan 'catalog.json') -Encoding utf8
    Assert-CatalogFailure 'orphan' 'Orphan license catalog entry'

    $ambiguous = Join-Path $scratch 'ambiguous'
    New-Item -ItemType Directory -Path $ambiguous | Out-Null
    @{
        entries = @(
            @{ packageNames = @('Avalonia'); version = '12.0.4'; spdx = 'MIT'; repository = 'https://example.invalid/one'; localFiles = @('eng/license-texts/dotnet-foundation-mit.txt') },
            @{ packagePatterns = @('Avalonia'); versions = @('12.0.4'); spdx = 'MIT'; repository = 'https://example.invalid/two'; localFiles = @('eng/license-texts/dotnet-foundation-mit.txt') }
        )
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $ambiguous 'catalog.json') -Encoding utf8
    Assert-CatalogFailure 'ambiguous' 'Ambiguous license catalog mapping'

    'Validated the real catalog and fail-closed malformed, orphan, and ambiguity cases.'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
