<#
.SYNOPSIS
  Re-cuts src/WinShot/Assets/winshot.ico from code at all seven shipped sizes.

  The mark is WinShot's own UI vocabulary: the region-selection corner bracket the
  capture selector draws, plus the rounded thumbnail the quick-actions card shows,
  on a #0A84FF squircle. One accent, one mark — the exe, the tray and the About
  pane all read from this file.

  Run after changing the geometry below:  powershell -File tools\make-icon.ps1
#>
[CmdletBinding()]
param([string]$OutPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutPath) { $OutPath = Join-Path $repo 'src\WinShot\Assets\winshot.ico' }

# ThemePalette.Accent / Theme.xaml AccentColor. The only blue in the mark.
$accent = [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF)
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-MarkBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [double]$size

    # Squircle body.
    $inset = $s * 0.02
    $body = New-RoundedPath $inset $inset ($s - $inset * 2) ($s - $inset * 2) ($s * 0.225)
    $brush = New-Object System.Drawing.SolidBrush($accent)
    $g.FillPath($brush, $body)

    # Thumbnail card, bottom-right, the same rounded rectangle the quick-actions card uses.
    $card = New-RoundedPath ($s * 0.395) ($s * 0.395) ($s * 0.400) ($s * 0.330) ([Math]::Max(1.0, $s * 0.065))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($white, $card)

    # Region-selection corner bracket, top-left, drawn over the card's corner.
    $wgt = [Math]::Max(1.4, $s * 0.088)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $wgt)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Square
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Square
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
    $a = $s * 0.205 + $wgt / 2
    $arm = $s * 0.235
    $bracket = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bracket.AddLine([single]$a, [single]($a + $arm), [single]$a, [single]$a)
    $bracket.AddLine([single]$a, [single]$a, [single]($a + $arm), [single]$a)
    $g.DrawPath($pen, $bracket)

    $bracket.Dispose(); $pen.Dispose(); $white.Dispose(); $brush.Dispose()
    $card.Dispose(); $body.Dispose(); $g.Dispose()
    return $bmp
}

$pngs = foreach ($size in $sizes) {
    $bmp = New-MarkBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICONDIR + ICONDIRENTRY per image, PNG payloads (what Windows Vista+ expects for
# every size, and what the previous asset already used).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = $sizes[$i]
    $w.Write([byte]($(if ($side -ge 256) { 0 } else { $side })))
    $w.Write([byte]($(if ($side -ge 256) { 0 } else { $side })))
    $w.Write([byte]0)            # palette count
    $w.Write([byte]0)            # reserved
    $w.Write([uint16]1)          # colour planes
    $w.Write([uint16]32)         # bits per pixel
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Host "Wrote $OutPath ($($sizes.Count) sizes: $($sizes -join ', '))"
