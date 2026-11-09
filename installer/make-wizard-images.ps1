# Regenerates the installer's wizard images from the app icon's source PNG
# (icon-source.png, 512 px, made by make-icon.ps1). Run again if the icon changes. Output: installer\assets\wizard-*.png
#
#   wizard-large-*.png  Left-hand image on the "Installation Complete" page: a slate gradient
#                       with the icon centred. Opaque, so Setup never has to blend it.
#   wizard-small-*.png  Top-right image on the other pages: the icon itself, transparent.
#
# Each comes in one size per DPI setting (100-250%); Setup picks the one for the display and,
# with WizardImageStretch=no, draws it unscaled.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Force $outDir | Out-Null
$icon = [Drawing.Bitmap]::FromFile((Join-Path $root 'icon-source.png'))

function New-Canvas([int]$w, [int]$h) {
    $bmp = [Drawing.Bitmap]::new($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    return $bmp, $g
}

function Save-Png([Drawing.Bitmap]$bmp, [string]$name) {
    $bmp.Save((Join-Path $outDir $name), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Image-area sizes per DPI, measured for WizardSizePercent=100.
$sizes = [ordered]@{
    '100' = @{ Large = 170, 320; Small = 58 }
    '125' = @{ Large = 225, 425; Small = 77 }
    '150' = @{ Large = 280, 529; Small = 97 }
    '175' = @{ Large = 334, 634; Small = 116 }
    '200' = @{ Large = 356, 676; Small = 124 }
    '225' = @{ Large = 411, 781; Small = 143 }
    '250' = @{ Large = 441, 837; Small = 159 }
}
$top = [Drawing.Color]::FromArgb(78, 88, 99)
$bottom = [Drawing.Color]::FromArgb(36, 41, 47)

Get-ChildItem $outDir -Filter 'wizard-*.png' | Remove-Item
foreach ($pct in $sizes.Keys) {
    $w, $h = $sizes[$pct].Large
    $bmp, $g = New-Canvas $w $h
    $brush = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Rectangle]::new(0, 0, $w, $h), $top, $bottom, 90)
    $g.FillRectangle($brush, 0, 0, $w, $h)
    $is = [int]($w * 0.62)
    $g.DrawImage($icon, [Drawing.Rectangle]::new([int](($w - $is) / 2), [int](($h - $is) * 0.42), $is, $is))
    $g.Dispose(); Save-Png $bmp "wizard-large-$pct.png"

    $s = $sizes[$pct].Small
    $bmp, $g = New-Canvas $s $s
    $pad = [int]($s * 0.06)
    $g.DrawImage($icon, [Drawing.Rectangle]::new($pad, $pad, $s - 2 * $pad, $s - 2 * $pad))
    $g.Dispose(); Save-Png $bmp "wizard-small-$pct.png"
}

$icon.Dispose()
Get-ChildItem $outDir -Filter 'wizard-*.png' | ForEach-Object { $_.Name }
