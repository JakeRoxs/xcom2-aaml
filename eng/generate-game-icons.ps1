#Requires -Version 7.0
<#
.SYNOPSIS
    Generates XCOM-inspired active-game icons for the AAML shell.
.DESCRIPTION
    Creates a distinct tactical badge for each supported game variant using a
    steel-and-armor aesthetic that stays readable at small sizes and works with
    the Avalonia asset pipeline.
.EXAMPLE
    .\eng\generate-game-icons.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing.Common

$OutputDirectory = Join-Path $PSScriptRoot '..\src\AAML.Avalonia\Assets\games'
$resolved = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolved -Force | Out-Null

$Variants = @(
    @{ FileName = 'game-xcom2.png'; BaseColor = [System.Drawing.Color]::FromArgb(255, 18, 40, 72); Accent = [System.Drawing.Color]::FromArgb(255, 180, 205, 230); Highlight = [System.Drawing.Color]::FromArgb(255, 120, 200, 255); Symbol = 'X2'; Secondary = [System.Drawing.Color]::FromArgb(255, 60, 120, 170) },
    @{ FileName = 'game-xcom2-wotc.png'; BaseColor = [System.Drawing.Color]::FromArgb(255, 110, 58, 24); Accent = [System.Drawing.Color]::FromArgb(255, 255, 210, 120); Highlight = [System.Drawing.Color]::FromArgb(255, 255, 165, 60); Symbol = 'W'; Secondary = [System.Drawing.Color]::FromArgb(255, 170, 88, 38) },
    @{ FileName = 'game-xcom2-wotc-challenge.png'; BaseColor = [System.Drawing.Color]::FromArgb(255, 84, 18, 24); Accent = [System.Drawing.Color]::FromArgb(255, 245, 180, 180); Highlight = [System.Drawing.Color]::FromArgb(255, 255, 115, 90); Symbol = 'C'; Secondary = [System.Drawing.Color]::FromArgb(255, 160, 54, 54) },
    @{ FileName = 'game-chimera.png'; BaseColor = [System.Drawing.Color]::FromArgb(255, 18, 64, 58); Accent = [System.Drawing.Color]::FromArgb(255, 200, 245, 230); Highlight = [System.Drawing.Color]::FromArgb(255, 110, 255, 190); Symbol = 'S'; Secondary = [System.Drawing.Color]::FromArgb(255, 32, 120, 104) }
)

foreach ($variant in $Variants) {
    $path = Join-Path $resolved $variant.FileName
    $bitmap = [System.Drawing.Bitmap]::new(128, 128, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, 128, 128),
        [System.Drawing.Color]::FromArgb(255, 8, 12, 18),
        $variant.BaseColor,
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $graphics.FillRectangle($background, 0, 0, 128, 128)

    $outerPoints = [System.Drawing.Point[]]::new(10)
    $outerPoints[0] = [System.Drawing.Point]::new(64, 10)
    $outerPoints[1] = [System.Drawing.Point]::new(102, 24)
    $outerPoints[2] = [System.Drawing.Point]::new(112, 58)
    $outerPoints[3] = [System.Drawing.Point]::new(104, 90)
    $outerPoints[4] = [System.Drawing.Point]::new(90, 110)
    $outerPoints[5] = [System.Drawing.Point]::new(64, 118)
    $outerPoints[6] = [System.Drawing.Point]::new(38, 110)
    $outerPoints[7] = [System.Drawing.Point]::new(24, 90)
    $outerPoints[8] = [System.Drawing.Point]::new(16, 58)
    $outerPoints[9] = [System.Drawing.Point]::new(26, 24)
    $graphics.FillPolygon([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, $variant.Highlight.R, $variant.Highlight.G, $variant.Highlight.B)), $outerPoints)
    $graphics.DrawPolygon([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 220, 230, 245), 3.5), $outerPoints)

    $rim = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(180, 16, 24, 32), 7.0)
    $graphics.DrawPolygon($rim, $outerPoints)

    $innerPoints = [System.Drawing.Point[]]::new(8)
    $innerPoints[0] = [System.Drawing.Point]::new(64, 24)
    $innerPoints[1] = [System.Drawing.Point]::new(88, 36)
    $innerPoints[2] = [System.Drawing.Point]::new(96, 62)
    $innerPoints[3] = [System.Drawing.Point]::new(86, 90)
    $innerPoints[4] = [System.Drawing.Point]::new(64, 101)
    $innerPoints[5] = [System.Drawing.Point]::new(42, 90)
    $innerPoints[6] = [System.Drawing.Point]::new(32, 62)
    $innerPoints[7] = [System.Drawing.Point]::new(40, 36)
    $graphics.FillPolygon([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, $variant.Secondary.R, $variant.Secondary.G, $variant.Secondary.B)), $innerPoints)
    $graphics.DrawPolygon([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(200, 255, 255, 255), 2.0), $innerPoints)

    $badge = New-Object System.Drawing.Font('Segoe UI', 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $graphics.DrawString($variant.Symbol, $badge, [System.Drawing.Brushes]::White, 64, 62, $format)

    $stripe = [System.Drawing.RectangleF]::new(30, 20, 68, 12)
    $graphics.FillRectangle([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(200, 255, 255, 255)), $stripe)

    $cut = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(90, 0, 0, 0), 3.0)
    $graphics.DrawLine($cut, 30, 76, 98, 76)

    $metal = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(130, 255, 255, 255), 1.5)
    $graphics.DrawLine($metal, 24, 48, 104, 48)
    $graphics.DrawLine($metal, 24, 76, 104, 76)

    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    $image = [System.Drawing.Image]::FromFile($path)
    $image.Dispose()
    if ((Get-Item $path).Length -le 1024) {
        throw "Generated icon '$path' is unexpectedly small; the PNG output is invalid."
    }

    Write-Host "Wrote $($variant.FileName)" -ForegroundColor Green
}

Write-Host "Generated valid game icon PNGs in $resolved" -ForegroundColor Green
