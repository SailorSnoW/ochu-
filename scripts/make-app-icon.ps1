<#
.SYNOPSIS
    Writes app/assets/icon-256.png and app/assets/icon.ico (the window and file icon of the
    programs of app/) from the project's logo, assets/logo.png. Run after a change of the logo.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Logo = Join-Path $Root 'assets\logo.png'
$OutDir = Join-Path $Root 'app\assets'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null
$source = [Drawing.Image]::FromFile($Logo)
function Resize([int]$size) {
    $bitmap = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}
$sizes = 256, 64, 48, 32, 16
$images = @{}
foreach ($s in $sizes) { $images[$s] = Resize $s }
[IO.File]::WriteAllBytes((Join-Path $OutDir 'icon-256.png'), $images[256])
# ICO with PNG entries.
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -eq 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$s].Length); $w.Write([uint32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.ico'), $out.ToArray())
$source.Dispose()
Write-Host ('Wrote icon-256.png and icon.ico to {0}' -f $OutDir)
