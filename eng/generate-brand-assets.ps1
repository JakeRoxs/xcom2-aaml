param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets/branding/generated')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root 'assets/branding/aaml-icon.svg'
$provenancePath = Join-Path $root 'assets/branding/provenance.json'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'Canonical SVG source is missing.' }
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) { throw 'Brand provenance is missing.' }
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
$rasterizerSourceHash = 'fbb7e2dd3b2845bff52e9770ac1ec895e1e38f73e47a44cbc18490b035f78808'
if ($sourceHash -cne $rasterizerSourceHash) {
    throw 'Canonical SVG changed without updating the matching deterministic rasterizer geometry and source hash.'
}

$rasterizerSource = @'
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

public static class AamlBrandRasterizer
{
    private readonly record struct Color(byte R, byte G, byte B, byte A = 255);

    public static byte[] RenderPng(int size)
    {
        var pixels = new byte[size * size * 4];
        const int samples = 4;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var totals = new double[4];
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var px = (x + (sx + 0.5) / samples) * 256.0 / size;
                var py = (y + (sy + 0.5) / samples) * 256.0 / size;
                var color = Sample(px, py);
                totals[0] += color.R;
                totals[1] += color.G;
                totals[2] += color.B;
                totals[3] += color.A;
            }
            var index = (y * size + x) * 4;
            var count = samples * samples;
            pixels[index] = (byte)Math.Round(totals[0] / count, MidpointRounding.AwayFromZero);
            pixels[index + 1] = (byte)Math.Round(totals[1] / count, MidpointRounding.AwayFromZero);
            pixels[index + 2] = (byte)Math.Round(totals[2] / count, MidpointRounding.AwayFromZero);
            pixels[index + 3] = (byte)Math.Round(totals[3] / count, MidpointRounding.AwayFromZero);
        }
        return EncodePng(size, pixels);
    }

    public static byte[] CreateIco(int[] sizes, byte[][] images)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        return stream.ToArray();
    }

    private static Color Sample(double x, double y)
    {
        var color = new Color(0, 0, 0, 0);
        if (RoundedRect(x, y, 8, 8, 240, 240, 54)) color = new Color(51, 65, 85);
        if (RoundedRect(x, y, 16, 16, 224, 224, 46)) color = new Color(17, 24, 39);

        if (Capsule(x, y, 121, 76, 139, 76, 12) || Capsule(x, y, 139, 76, 158, 128, 12) ||
            Capsule(x, y, 121, 128, 158, 128, 12) || Capsule(x, y, 121, 180, 139, 180, 12) ||
            Capsule(x, y, 139, 180, 158, 128, 12)) color = new Color(45, 212, 191);
        if (Circle(x, y, 158, 128, 17)) color = new Color(94, 234, 212);

        if (RoundedRect(x, y, 36, 56, 94, 40, 12)) color = new Color(34, 211, 238);
        if (RoundedRect(x, y, 36, 108, 94, 40, 12)) color = new Color(94, 234, 212);
        if (RoundedRect(x, y, 36, 160, 94, 40, 12)) color = new Color(56, 189, 248);
        if (RoundedRect(x, y, 48, 69, 49, 14, 7) || RoundedRect(x, y, 48, 121, 49, 14, 7) ||
            RoundedRect(x, y, 48, 173, 49, 14, 7)) color = new Color(15, 37, 51);

        if (Triangle(x, y, 146, 80, 224, 128, 146, 176)) color = new Color(251, 146, 60);
        if (Triangle(x, y, 169, 107, 204, 128, 169, 149)) color = new Color(254, 243, 199);
        return color;
    }

    private static bool RoundedRect(double x, double y, double left, double top, double width, double height, double radius)
    {
        var cx = Math.Clamp(x, left + radius, left + width - radius);
        var cy = Math.Clamp(y, top + radius, top + height - radius);
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static bool Circle(double x, double y, double cx, double cy, double radius)
    {
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static bool Capsule(double x, double y, double ax, double ay, double bx, double by, double width)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1);
        return Circle(x, y, ax + t * dx, ay + t * dy, width / 2);
    }

    private static bool Triangle(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
    {
        static double Edge(double px, double py, double x1, double y1, double x2, double y2) => (px - x2) * (y1 - y2) - (x1 - x2) * (py - y2);
        var d1 = Edge(x, y, ax, ay, bx, by);
        var d2 = Edge(x, y, bx, by, cx, cy);
        var d3 = Edge(x, y, cx, cy, ax, ay);
        return !(d1 < 0 || d2 < 0 || d3 < 0) || !(d1 > 0 || d2 > 0 || d3 > 0);
    }

    private static byte[] EncodePng(int size, byte[] pixels)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * size * 4, size * 4);
        }
        var compressed = EncodeZlibStored(raw.ToArray());
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), size);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] EncodeZlibStored(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        var offset = 0;
        while (offset < data.Length)
        {
            var length = Math.Min(65535, data.Length - offset);
            output.WriteByte((byte)(offset + length == data.Length ? 1 : 0));
            output.WriteByte((byte)length);
            output.WriteByte((byte)(length >> 8));
            var inverse = (ushort)~length;
            output.WriteByte((byte)inverse);
            output.WriteByte((byte)(inverse >> 8));
            output.Write(data, offset, length);
            offset += length;
        }
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, (b << 16) | a);
        output.Write(checksum);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
'@

Add-Type -TypeDefinition $rasterizerSource -Language CSharp

$pngSizes = @(16, 32, 48, 64, 128, 256, 512)
$icoSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngDirectory = Join-Path $OutputDirectory 'png'
New-Item -ItemType Directory -Path $pngDirectory -Force | Out-Null

$pngBySize = @{}
foreach ($size in @($pngSizes + $icoSizes | Sort-Object -Unique)) {
    $pngBySize[$size] = [AamlBrandRasterizer]::RenderPng($size)
}
foreach ($size in $pngSizes) {
    [System.IO.File]::WriteAllBytes((Join-Path $pngDirectory "aaml-$size.png"), $pngBySize[$size])
}
$icoImages = [byte[][]]::new($icoSizes.Count)
for ($index = 0; $index -lt $icoSizes.Count; $index++) {
    $icoImages[$index] = $pngBySize[$icoSizes[$index]]
}
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'aaml.ico'), [AamlBrandRasterizer]::CreateIco($icoSizes, $icoImages))

$files = [ordered]@{}
$files['aaml-icon.svg'] = [ordered]@{ sha256 = $sourceHash; kind = 'source'; scalable = $true }
$files['aaml.ico'] = [ordered]@{ sha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'aaml.ico') -Algorithm SHA256).Hash.ToLowerInvariant(); kind = 'windows-icon'; frames = $icoSizes }
foreach ($size in $pngSizes) {
    $relative = "png/aaml-$size.png"
    $files[$relative] = [ordered]@{ sha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $relative) -Algorithm SHA256).Hash.ToLowerInvariant(); kind = 'png'; width = $size; height = $size }
}
[ordered]@{
    schemaVersion = 1
    source = 'assets/branding/aaml-icon.svg'
    generator = 'eng/generate-brand-assets.ps1'
    files = $files
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'asset-manifest.json') -Encoding utf8

"Generated AAML brand assets in $OutputDirectory"
