# Generates a Windows shell overlay icon from the main PowerLink icon.
# Overlay icons need the badge graphic in the lower-LEFT of an otherwise
# transparent canvas — Windows draws the icon as-is on top of file icons,
# so a fully-painted source like the app icon ends up looking centered.
#
# Output: src/PowerLink.ShellExt/assets/hardlink-overlay.ico
# Multi-resolution (16, 24, 32, 48, 64) with the source scaled to ~60%
# and anchored to the bottom-left.

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\PowerLink.App\Assets\Icon.ico'),
    [string]$Output = (Join-Path $PSScriptRoot '..\src\PowerLink.ShellExt\assets\hardlink-overlay.ico'),
    [int[]]$Sizes = @(16, 24, 32, 48, 64),
    [double]$Scale = 0.6
)

Add-Type -AssemblyName System.Drawing

$Source = [System.IO.Path]::GetFullPath($Source)
$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -Path (Split-Path $Output -Parent) -ItemType Directory -Force | Out-Null

$srcIcon = [System.Drawing.Icon]::new($Source)
$srcBmp  = $srcIcon.ToBitmap()

$frames = @()
foreach ($size in $Sizes) {
    $canvas = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $badge = [int][Math]::Round($size * $Scale)
    $rect  = [System.Drawing.Rectangle]::new(0, $size - $badge, $badge, $badge)
    $g.DrawImage($srcBmp, $rect)
    $g.Dispose()

    $png = [System.IO.MemoryStream]::new()
    $canvas.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()

    $frames += [pscustomobject]@{ Size = $size; Bytes = $png.ToArray() }
}

$srcBmp.Dispose()
$srcIcon.Dispose()

# ICO file format: header (6) + directory entries (16 each) + image blobs.
$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)

# ICONDIR
$bw.Write([uint16]0)              # Reserved
$bw.Write([uint16]1)              # Type = ICO
$bw.Write([uint16]$frames.Count)

# ICONDIRENTRY
$dataOffset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $w = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $h = $w
    $bw.Write([byte]$w)            # Width  (0 = 256)
    $bw.Write([byte]$h)            # Height (0 = 256)
    $bw.Write([byte]0)             # Palette colors (0 for 32bpp)
    $bw.Write([byte]0)             # Reserved
    $bw.Write([uint16]1)           # Color planes
    $bw.Write([uint16]32)          # Bits per pixel
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$dataOffset)
    $dataOffset += $f.Bytes.Length
}

# Image blobs (PNG-encoded — supported by Windows Vista+)
foreach ($f in $frames) {
    $bw.Write($f.Bytes)
}

[System.IO.File]::WriteAllBytes($Output, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Host ("Wrote {0} ({1} bytes, {2} sizes: {3})" -f
    $Output, (Get-Item $Output).Length, $Sizes.Count, ($Sizes -join ', '))
