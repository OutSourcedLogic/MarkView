# Draws MarkView's icon: a document page with a folded corner, a bold "M" and a down arrow
# (the Markdown mark). Original artwork, MIT-licensed with the rest of the repo.
#
#   .\installer\make-icon.ps1
#
# Output: icon-source.png (512 px, used by make-wizard-images.ps1) and MarkView.ico
# (16-256 px, each size drawn directly rather than scaled down, so small sizes stay crisp).
# Run installer\make-wizard-images.ps1 afterwards to refresh the installer images.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent

$ink   = [Drawing.Color]::FromArgb(0, 0, 2)          # outlines and the M
$paper = [Drawing.Color]::FromArgb(226, 232, 235)    # page
$fold  = [Drawing.Color]::FromArgb(104, 176, 250)    # folded corner
$arrow = [Drawing.Color]::FromArgb(6, 137, 241)      # down arrow

function New-IconBitmap([int]$size) {
    $bmp = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    # Everything below is laid out on a 512-unit grid.
    $g.ScaleTransform($size / 512, $size / 512)

    # Thicker lines at small sizes so they survive at 16-32 px.
    $w = if ($size -le 24) { 46 } elseif ($size -le 48) { 38 } else { 30 }
    $pen = [Drawing.Pen]::new($ink, $w)
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'

    # Page with the top-right corner folded over.
    $l = 78; $t = 28; $r = 434; $b = 484; $f = 116
    $page = [Drawing.Drawing2D.GraphicsPath]::new()
    $page.AddLines([Drawing.PointF[]]@(
        [Drawing.PointF]::new($l, $t), [Drawing.PointF]::new($r - $f, $t),
        [Drawing.PointF]::new($r, $t + $f), [Drawing.PointF]::new($r, $b),
        [Drawing.PointF]::new($l, $b)))
    $page.CloseFigure()
    $g.FillPath([Drawing.SolidBrush]::new($paper), $page)

    $flap = [Drawing.Drawing2D.GraphicsPath]::new()
    $flap.AddLines([Drawing.PointF[]]@(
        [Drawing.PointF]::new($r - $f, $t), [Drawing.PointF]::new($r - $f, $t + $f),
        [Drawing.PointF]::new($r, $t + $f)))
    $flap.CloseFigure()
    $g.FillPath([Drawing.SolidBrush]::new($fold), $flap)
    $g.DrawPath($pen, $flap)
    $g.DrawPath($pen, $page)

    # Bold M.
    $mt = 220; $mb = 400
    $g.DrawLines($pen, [Drawing.PointF[]]@(
        [Drawing.PointF]::new(132, $mb), [Drawing.PointF]::new(132, $mt),
        [Drawing.PointF]::new(197, 306), [Drawing.PointF]::new(262, $mt),
        [Drawing.PointF]::new(262, $mb)))

    # Down arrow: shaft plus a filled head, outlined in ink like the rest.
    $ax = 350
    $shaft = [Drawing.Pen]::new($arrow, $w * 1.35)
    $shaft.StartCap = 'Round'; $shaft.EndCap = 'Flat'
    $g.DrawLine($shaft, $ax, $mt, $ax, 318)
    $head = [Drawing.PointF[]]@(
        [Drawing.PointF]::new($ax - 58, 312), [Drawing.PointF]::new($ax + 58, 312),
        [Drawing.PointF]::new($ax, $mb + 6))
    $g.FillPolygon([Drawing.SolidBrush]::new($arrow), $head)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes([Drawing.Bitmap]$bmp) {
    $ms = [IO.MemoryStream]::new()
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# 512 px source image for the installer artwork.
$src = New-IconBitmap 512
$src.Save((Join-Path $root 'icon-source.png'), [Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()

# Multi-size .ico with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { $bmp = New-IconBitmap $s; , (Get-PngBytes $bmp); $bmp.Dispose() }

$out = [IO.MemoryStream]::new()
$bw = [IO.BinaryWriter]::new($out)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'MarkView.ico'), $out.ToArray())
$bw.Dispose()

"Wrote icon-source.png and MarkView.ico ($($sizes -join ', ') px)"
