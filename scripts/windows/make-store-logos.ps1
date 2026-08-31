# Generates the optional Store-listing logo images Partner Center's "Store
# logos" section asks for (Product release > Store listings > Store logos).
# These are upload-only display assets for the Store page itself — not part
# of the shipped app or the MSIX package (see scripts/windows/build-msix.ps1
# and scripts/windows/make-app-icons.ps1 for those), and not wired into any
# build. The output is checked into design/store/ (regenerate + commit after
# updating design/masters/icon/icon-1024.png) so Partner Center re-uploads are
# a copy-paste, not a re-derivation from a script no one remembers to run.
#
# Usage:
#   pwsh scripts/windows/make-store-logos.ps1
#   pwsh scripts/windows/make-store-logos.ps1 -OutDir path\to\other\output
#
# Windows-only (System.Drawing/GDI+). The 9:16 poster additionally needs
# Inkscape, to rasterise design/masters/logo/wordmark-dark.svg (run
# scripts/design/make-masters.ps1 first if that file is stale).
param(
    [string]$OutDir,
    # Override when Inkscape lives somewhere else (or is only on PATH).
    [string]$Inkscape
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) {
    $OutDir = Join-Path $repo 'design\store'
}
$logoDir = Join-Path $repo 'design\masters\logo'
$src  = New-Object System.Drawing.Bitmap((Join-Path $repo 'design\masters\icon\icon-1024.png'))
# Poster fill: must match the badge ring's navy (#242b36) so the circular tile
# blends into the poster instead of showing as a mismatched dark-on-dark blob.
# icon-1024 is a circular badge with transparent corners (2026-07), not
# full-bleed, so this can't be sampled from a corner pixel (transparent) —
# keep it hardcoded in sync with design/masters/icon's ink-dark color.
$bg   = [System.Drawing.Color]::FromArgb(255, 0x24, 0x2b, 0x36)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $Inkscape) {
    $candidates = @(
        'C:\Program Files\Inkscape\bin\inkscape.com',
        'C:\Program Files (x86)\Inkscape\bin\inkscape.com') +
        @((Get-Command inkscape -ErrorAction SilentlyContinue).Source)
    $Inkscape = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Inkscape) { throw "Inkscape not found. Install it or pass -Inkscape <path to inkscape.com>." }

# Square full-bleed tile at a given size, rendered from the 1024 master.
function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$name) {
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "wrote $path"
}

# --- square logos: Box art + the three "Store display images" tiers ---
foreach ($pair in @(
        @{ Size = 2160; Name = 'BoxArt-1x1-2160x2160.png' },
        @{ Size = 300;  Name = 'AppTileIcon-1x1-300x300.png' },
        @{ Size = 150;  Name = 'Square-1x1-150x150.png' },
        @{ Size = 71;   Name = 'Square-1x1-71x71.png' })) {
    $tile = New-Tile $pair.Size
    Save-Png $tile $pair.Name
    $tile.Dispose()
}

# --- 9:16 poster art: the "pgNimbus" wordmark + tagline, centred on a canvas
#     filled with the same dark navy card design/masters/logo/social-preview.png
#     uses - not the bare tile alone, so the poster reads as a product card
#     rather than a stretched icon. Reuses the generated wordmark-dark.svg
#     (mark + light-on-dark "pgNimbus" text) rather than re-deriving the
#     lockup a third time.
$pw = 1440; $ph = 2160
$poster = New-Object System.Drawing.Bitmap($pw, $ph, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($poster)
$g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g.TextRenderingHint  = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear($bg)

$posterTmp = Join-Path ([System.IO.Path]::GetTempPath()) ("pgnimbus-poster-" + [guid]::NewGuid().ToString('n') + '.png')
$lockupH = 300
& $Inkscape (Join-Path $logoDir 'wordmark-dark.svg') '--export-type=png' "--export-filename=$posterTmp" '-h' "$lockupH" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "inkscape failed to rasterise wordmark-dark.svg" }
$lockup = New-Object System.Drawing.Bitmap($posterTmp)

$tagline   = 'Fast, modern PostgreSQL client for Windows & macOS'
$tagFont   = New-Object System.Drawing.Font('Segoe UI', 46, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$tagBrush  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xAA, 0xB2, 0xC0))
$tagFormat = New-Object System.Drawing.StringFormat
$tagFormat.Alignment = [System.Drawing.StringAlignment]::Center
$tagGap   = 100.0   # lockup bottom -> tagline top
$tagRectH = 80.0

$blockH  = $lockup.Height + $tagGap + $tagRectH
$lockupX = [int](($pw - $lockup.Width) / 2)
$lockupY = [int](($ph - $blockH) / 2)
$g.DrawImage($lockup, $lockupX, $lockupY, $lockup.Width, $lockup.Height)
$tagRect = New-Object System.Drawing.RectangleF(0, ($lockupY + $lockup.Height + $tagGap), $pw, $tagRectH)
$g.DrawString($tagline, $tagFont, $tagBrush, $tagRect, $tagFormat)

$g.Dispose()
$lockup.Dispose(); $tagFont.Dispose(); $tagBrush.Dispose(); $tagFormat.Dispose()
Remove-Item -Force $posterTmp
Save-Png $poster 'Poster-9x16-1440x2160.png'
$poster.Dispose()

$src.Dispose()
