# Generates the 16:9 "Super hero art" Partner Center asks for when a listing has
# a trailer (Product release > Store listings > Trailers and additional assets).
# The Store shows it at the top of the listing after the trailer finishes, with
# its own title, rating and Install button laid over the left side, so:
#   - it must not include the product's title (Store rule), which is why this
#     is the app on the brand navy and not the wordmark lockup the poster uses;
#   - the left third is kept empty for that overlay.
# The window is design/store/screenshots/02-query-plan.dark.png, which the
# headless harness renders, so regenerating the screenshots and then this keeps
# the two in step. Output is checked into design/store/ like the other logos.
#
# Usage:
#   pwsh scripts/windows/make-store-hero.ps1
#   pwsh scripts/windows/make-store-hero.ps1 -Size 3840x2160
#
# Windows-only (System.Drawing/GDI+).
param(
    [string]$OutDir,
    [string]$Screenshot,
    [ValidateSet('1920x1080', '3840x2160')] [string]$Size = '1920x1080'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) { $OutDir = Join-Path $repo 'design\store' }
if (-not $Screenshot) { $Screenshot = Join-Path $repo 'design\store\screenshots\02-query-plan.dark.png' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$W, $H = $Size.Split('x') | ForEach-Object { [int]$_ }
$k = $W / 1920.0   # everything below is laid out for 1920x1080 and scaled

# The same navy the poster and the social card use (make-store-logos.ps1).
$navy  = [System.Drawing.Color]::FromArgb(255, 0x24, 0x2b, 0x36)
$navy2 = [System.Drawing.Color]::FromArgb(255, 0x1a, 0x1f, 0x28)

$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

# Background: navy, a little darker towards the bottom left where the Store's text sits.
$full = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($full, $navy, $navy2, [single]125)
$g.FillRectangle($bgBrush, $full)

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# The window: right two thirds, running off the right edge.
$shot = New-Object System.Drawing.Bitmap($Screenshot)
$ww = [single](1400 * $k); $wh = [single]($ww * $shot.Height / $shot.Width)
$wx = [single](640 * $k);  $wy = [single](($H - $wh) / 2)
$r  = [single](14 * $k)

# A stacked drop shadow. (No radial glow: GDI+ draws a dark radial gradient in visible
# 8-bit rings.)
for ($i = 12; $i -ge 1; $i--) {
    $o = [single]($i * 3 * $k)
    $sp = New-RoundRect ($wx - $o) ($wy - $o + 10 * $k) ($ww + 2 * $o) ($wh + 2 * $o) ($r + $o)
    $sb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(9, 0, 0, 0))
    $g.FillPath($sb, $sp); $sb.Dispose(); $sp.Dispose()
}

$win = New-RoundRect $wx $wy $ww $wh $r
$g.SetClip($win)
$g.DrawImage($shot, $wx, $wy, $ww, $wh)
$g.ResetClip()
$edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [single](1.5 * $k))
$g.DrawPath($edge, $win)

$name = "SuperHeroArt-16x9-$Size.png"
$path = Join-Path $OutDir $name
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "wrote $path"

$edge.Dispose(); $win.Dispose(); $shot.Dispose()
$bgBrush.Dispose(); $g.Dispose(); $bmp.Dispose()
