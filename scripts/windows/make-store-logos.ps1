# Generates the optional Store-listing logo images Partner Center's "Store
# logos" section asks for (Product release > Store listings > Store logos).
# These are upload-only display assets for the Store page itself — not part
# of the shipped app or the MSIX package (see scripts/windows/build-msix.ps1
# and scripts/windows/make-app-icons.ps1 for those) — so this writes to
# -OutDir rather than PgNimbus.App/Assets, and isn't wired into any build.
#
# Usage:
#   pwsh scripts/windows/make-store-logos.ps1 -OutDir path\to\output
#
# Windows-only (System.Drawing/GDI+).
param(
    [Parameter(Mandatory)] [string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src  = New-Object System.Drawing.Bitmap((Join-Path $repo 'design\icon-tile.png'))
$bg   = [System.Drawing.Color]::FromArgb(255, 59, 68, 77)  # sampled from icon-tile.png's own background
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Rounded-corner tile at a given size, rendered fresh from the full-res
# source (same approach as make-app-icons.ps1's New-Tile).
function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $r = [Math]::Max(1, [Math]::Round($size * 0.22))
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $g.SetClip($path)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $path.Dispose()
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

# --- 9:16 poster art: tile centered on a canvas filled with its own
#     background color, so it reads as a poster rather than a stretched icon ---
$pw = 1440; $ph = 2160
$poster = New-Object System.Drawing.Bitmap($pw, $ph, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($poster)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear($bg)
$tileSize = [int]($pw * 0.62)
$tile = New-Tile $tileSize
$g.DrawImage($tile, [int](($pw - $tileSize) / 2), [int](($ph - $tileSize) / 2))
$g.Dispose()
$tile.Dispose()
Save-Png $poster 'Poster-9x16-1440x2160.png'
$poster.Dispose()

$src.Dispose()
