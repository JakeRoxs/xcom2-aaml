Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) "aaml-qualification-fixture-$([guid]::NewGuid().ToString('N'))"
try {
    $payload = Join-Path $root 'payload/win-x64'
    $extract = Join-Path $root 'extract'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    'fixture' | Set-Content -LiteralPath (Join-Path $payload 'fixture.txt') -Encoding ascii
    [ordered]@{ schemaVersion = 1; rid = 'win-x64'; version = '1.2.3'; repository = 'https://github.com/JakeRoxs/xcom2-aaml'; commit = '0123456789abcdef0123456789abcdef01234567'; selfContained = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'release-metadata.json') -Encoding utf8
    $checksumLines = Get-ChildItem -LiteralPath $payload -File | ForEach-Object { "$(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $($_.Name)" }
    $checksumLines | Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS') -Encoding ascii
    $archive = Join-Path $root 'fixture.zip'
    Compress-Archive -Path (Join-Path $root 'payload/win-x64') -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $result = & (Join-Path $PSScriptRoot 'assert-exact-artifact.ps1') -ArchivePath $archive -ExpectedArchiveSha256 $hash -ExpectedRepository 'https://github.com/JakeRoxs/xcom2-aaml' -ExpectedCommit '0123456789abcdef0123456789abcdef01234567' -ExpectedVersion '1.2.3' -ExtractionDirectory $extract
    if ($result.rid -ne 'win-x64') { throw 'Valid exact-artifact fixture was rejected.' }

    $failed = $false
    try { & (Join-Path $PSScriptRoot 'assert-exact-artifact.ps1') -ArchivePath $archive -ExpectedArchiveSha256 ('0' * 64) -ExpectedRepository 'https://github.com/JakeRoxs/xcom2-aaml' -ExpectedCommit '0123456789abcdef0123456789abcdef01234567' -ExpectedVersion '1.2.3' -ExtractionDirectory $extract } catch { $failed = $_.Exception.Message -match 'SHA-256 mismatch' }
    if (-not $failed) { throw 'Archive hash mismatch fixture did not fail closed.' }

    Add-Content -LiteralPath (Join-Path $payload 'fixture.txt') -Value 'tampered'
    Remove-Item -LiteralPath $archive -Force
    Compress-Archive -Path (Join-Path $root 'payload/win-x64') -DestinationPath $archive
    $tamperedArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'assert-exact-artifact.ps1') -ArchivePath $archive -ExpectedArchiveSha256 $tamperedArchiveHash -ExpectedRepository 'https://github.com/JakeRoxs/xcom2-aaml' -ExpectedCommit '0123456789abcdef0123456789abcdef01234567' -ExpectedVersion '1.2.3' -ExtractionDirectory $extract } catch { $failed = $_.Exception.Message -match 'SHA256SUMS mismatch' }
    if (-not $failed) { throw 'Payload tamper fixture did not fail closed.' }

    $workflow = Get-Content -LiteralPath (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.github/workflows/protected-qualification.yml') -Raw
    foreach ($required in @('workflow_dispatch:', 'aaml-windows-game', 'aaml-linux-proton', 'aaml-steam-mutation', 'environment:', 'actions: read', 'cancel-in-progress: false', 'retention-days: 30', 'github.workflow_sha')) {
        if ($workflow -notmatch [regex]::Escape($required)) { throw "Protected workflow fixture is missing: $required" }
    }
    if ($workflow -match '(?m)^\s*(pull_request|pull_request_target|push):') { throw 'Protected qualification must not have an automatic or PR trigger.' }
    'Qualification static fixtures passed: provenance, archive hash, payload tamper, protected labels, manual trigger, serialization, and retention.'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
