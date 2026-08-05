Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$branding = Join-Path $root 'assets/branding'
$generated = Join-Path $branding 'generated'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("aaml-brand-test-" + [Guid]::NewGuid().ToString('N'))

function Get-PngDimensions([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A') { throw "Not a PNG file: $Path" }
    $width = $bytes[16] * 16777216 + $bytes[17] * 65536 + $bytes[18] * 256 + $bytes[19]
    $height = $bytes[20] * 16777216 + $bytes[21] * 65536 + $bytes[22] * 256 + $bytes[23]
    return @($width, $height)
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected brand asset is missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Expected) { throw "Brand asset hash differs from its manifest: $Path" }
}

try {
    [xml]$svg = Get-Content -LiteralPath (Join-Path $branding 'aaml-icon.svg') -Raw
    if ($svg.svg.viewBox -ne '0 0 256 256') { throw 'Canonical SVG must use the 256-unit design grid.' }
    $svgText = $svg.OuterXml
    if ($svgText -match '<(?:image|text|use)\b|(?:href|font-family|url)\s*=') { throw 'Canonical SVG must contain only self-contained geometric primitives.' }

    $provenance = Get-Content -LiteralPath (Join-Path $branding 'provenance.json') -Raw | ConvertFrom-Json
    if ($provenance.repository -ne 'https://github.com/JakeRoxs/xcom2-aaml' -or $provenance.canonicalSource -ne 'assets/branding/aaml-icon.svg') { throw 'Brand provenance repository/source identity is invalid.' }
    if (@($provenance.externalAssets).Count -ne 0 -or @($provenance.legacyAssets).Count -ne 0) { throw 'Brand provenance declares external or legacy artwork.' }
    if ($provenance.declaration -notmatch 'without copying or tracing') { throw 'Brand provenance lacks an explicit originality declaration.' }

    $manifest = Get-Content -LiteralPath (Join-Path $generated 'asset-manifest.json') -Raw | ConvertFrom-Json
    foreach ($property in $manifest.files.PSObject.Properties) {
        $path = if ($property.Name -eq 'aaml-icon.svg') { Join-Path $branding $property.Name } else { Join-Path $generated $property.Name }
        Assert-Hash $path $property.Value.sha256
        if ($property.Value.kind -eq 'png') {
            $dimensions = Get-PngDimensions $path
            if ($dimensions[0] -ne $property.Value.width -or $dimensions[1] -ne $property.Value.height) { throw "PNG dimensions differ from manifest: $path" }
        }
    }

    $icoPath = Join-Path $generated 'aaml.ico'
    $icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
    $reader = [System.IO.BinaryReader]::new([System.IO.MemoryStream]::new($icoBytes))
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) { throw 'ICO header is invalid.' }
        $count = $reader.ReadUInt16()
        $expectedFrames = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
        if ($count -ne $expectedFrames.Count) { throw "ICO frame count is $count instead of $($expectedFrames.Count)." }
        $frames = @()
        for ($index = 0; $index -lt $count; $index++) {
            $width = $reader.ReadByte(); $height = $reader.ReadByte()
            $null = $reader.ReadByte(); $null = $reader.ReadByte(); $null = $reader.ReadUInt16(); $bitCount = $reader.ReadUInt16()
            $length = $reader.ReadUInt32(); $offset = $reader.ReadUInt32()
            $frameSize = if ($width -eq 0) { 256 } else { [int]$width }
            $frameHeight = if ($height -eq 0) { 256 } else { [int]$height }
            if ($frameHeight -ne $frameSize -or $bitCount -ne 32) { throw 'ICO frame geometry or bit depth is invalid.' }
            if ($offset + $length -gt $icoBytes.Length -or [Convert]::ToHexString($icoBytes[$offset..($offset + 7)]) -ne '89504E470D0A1A0A') { throw 'ICO frame is not a bounded PNG payload.' }
            $frames += $frameSize
        }
        if (Compare-Object $expectedFrames $frames) { throw "ICO frame sizes are invalid: $($frames -join ', ')." }
    }
    finally { $reader.Dispose() }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'generate-brand-assets.ps1') -OutputDirectory $scratch | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Brand asset regeneration failed.' }
    $expectedFiles = @(Get-ChildItem -LiteralPath $generated -File -Recurse | ForEach-Object { [System.IO.Path]::GetRelativePath($generated, $_.FullName).Replace('\', '/') } | Sort-Object)
    $actualFiles = @(Get-ChildItem -LiteralPath $scratch -File -Recurse | ForEach-Object { [System.IO.Path]::GetRelativePath($scratch, $_.FullName).Replace('\', '/') } | Sort-Object)
    if (Compare-Object $expectedFiles $actualFiles) { throw 'Regenerated brand asset file set differs from checked-in outputs.' }
    foreach ($relative in $expectedFiles) {
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $generated $relative) -Algorithm SHA256).Hash
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $scratch $relative) -Algorithm SHA256).Hash
        if ($expectedHash -cne $actualHash) { throw "Brand generation is not byte-reproducible: $relative" }
    }

    $project = Get-Content -LiteralPath (Join-Path $root 'src/AAML.Avalonia/AAML.Avalonia.csproj') -Raw
    $app = Get-Content -LiteralPath (Join-Path $root 'src/AAML.Avalonia/App.axaml.cs') -Raw
    if ($project -notmatch '<ApplicationIcon>\.\./\.\./assets/branding/generated/aaml\.ico</ApplicationIcon>') { throw 'Windows executable ApplicationIcon is not wired to the AAML ICO.' }
    $fatal = Get-Content -LiteralPath (Join-Path $root 'src/AAML.Avalonia/FatalErrorCoordinator.cs') -Raw
    if ($app -notmatch 'Icon\s*=\s*CreateWindowIcon\(' -or $app -notmatch 'internal static WindowIcon CreateWindowIcon\(' -or $app -notmatch 'avares://AAML\.Avalonia/Assets/aaml-icon\.png') { throw 'Avalonia main window icon is not wired through the shared icon factory.' }
    if ($fatal -notmatch 'Icon\s*=\s*App\.CreateWindowIcon\(') { throw 'Avalonia fatal-error window does not use the shared icon factory.' }

    $desktop = Get-Content -LiteralPath (Join-Path $root 'eng/linux/io.github.jakeroxs.xcom2_aaml.desktop') -Raw
    [xml]$appstream = Get-Content -LiteralPath (Join-Path $root 'eng/linux/io.github.jakeroxs.xcom2_aaml.metainfo.xml') -Raw
    if ($desktop -notmatch '(?m)^Icon=io\.github\.jakeroxs\.xcom2_aaml$' -or $desktop -notmatch '(?m)^Exec=AAML\.Avalonia$') { throw 'Linux desktop icon or executable reference is invalid.' }
    if ($appstream.component.id -ne 'io.github.jakeroxs.xcom2_aaml' -or $appstream.component.launchable.'#text' -ne 'io.github.jakeroxs.xcom2_aaml.desktop') { throw 'AppStream application or desktop ID is invalid.' }

    'Validated original provenance, deterministic generation, PNG dimensions, ICO frames, executable/window wiring, and Linux metadata.'
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
