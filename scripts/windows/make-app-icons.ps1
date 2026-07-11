# Assembles the shipped app icons from the prepared design masters. The
# masters are hand-drawn per size (see design/DESIGNER-BRIEF.md) — this script
# does NOT resample the small, legibility-critical sizes: it copies them
# verbatim and only downscales the larger, non-critical sizes from a master.
#
#   INPUT  design/masters/icon/icon-{16,24,32,48,256,1024}.png   square full-bleed tiles
#          design/masters/window/window-{light,dark}-256.png     transparent line art
#
#   OUTPUT PgNimbus.App/Assets/app.ico                exe + MSI icon (multi-size)
#          PgNimbus.App/Assets/icon-256.png           macOS .icns source (square)
#          PgNimbus.App/Assets/icon-256-light.png     light-theme window icon (transparent)
#          PgNimbus.App/Assets/icon-256-dark.png      dark-theme  window icon (transparent)
#          PgNimbus.App/Assets/Msix/Square44x44Logo.png    MSIX small tile
#          PgNimbus.App/Assets/Msix/Square150x150Logo.png  MSIX medium tile
#          PgNimbus.App/Assets/Msix/StoreLogo.png          MSIX / Store listing icon (50px)
#
# Windows-only (uses System.Drawing/GDI+). Run after the designer updates the
# masters:  pwsh scripts/windows/make-app-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo    = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$iconDir = Join-Path $repo 'design\masters\icon'
$winDir  = Join-Path $repo 'design\masters\window'
$outDir  = Join-Path $repo 'PgNimbus.App\Assets'
$msixDir = Join-Path $outDir 'Msix'

function Get-Master([int]$size) {
    $p = Join-Path $iconDir "icon-$size.png"
    if (-not (Test-Path $p)) { throw "Missing icon master: $p" }
    return $p
}

# A square tile bitmap at the requested size. If a hand-drawn master exists at
# exactly that size it is loaded as-is (no resample); otherwise it is
# high-quality-downscaled from `fromSize` (always a LARGER master, never
# upscaled) so glyph detail is preserved.
function Get-Tile([int]$size, [int]$fromSize) {
    $exact = Join-Path $iconDir "icon-$size.png"
    if (Test-Path $exact) {
        return New-Object System.Drawing.Bitmap($exact)
    }
    $src = New-Object System.Drawing.Bitmap((Get-Master $fromSize))
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose(); $src.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray(); $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

# Classic uncompressed ICO entry: BITMAPINFOHEADER + bottom-up BGRA + AND mask.
# PNG compression inside .ico is only spec-blessed for the 256px entry, so the
# smaller sizes go in as plain BMP for maximum shell compatibility.
function Get-BmpEntryBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)          # BITMAPINFOHEADER size
    $bw.Write([int32]$s)           # width
    $bw.Write([int32]($s * 2))     # height (XOR + AND mask)
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bpp
    $bw.Write([uint32]0)           # BI_RGB
    $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {       # bottom-up BGRA rows
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [Math]::Ceiling($s / 32.0) * 4  # 1bpp AND mask, rows padded to 32 bits
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    $bytes = $ms.ToArray(); $bw.Dispose(); $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

New-Item -ItemType Directory -Force -Path $msixDir | Out-Null

# --- per-theme window icons: copy the transparent 256px line art verbatim ---
foreach ($pair in @(
        @{ Src = 'window-light-256.png'; Dst = 'icon-256-light.png' },
        @{ Src = 'window-dark-256.png';  Dst = 'icon-256-dark.png' })) {
    $s = Join-Path $winDir $pair.Src
    if (-not (Test-Path $s)) { throw "Missing window master: $s" }
    Copy-Item $s (Join-Path $outDir $pair.Dst) -Force
    Write-Host "copied PgNimbus.App\Assets\$($pair.Dst)"
}

# --- icon-256.png (macOS .icns source): copy the 256 tile master verbatim ---
Copy-Item (Get-Master 256) (Join-Path $outDir 'icon-256.png') -Force
Write-Host 'copied PgNimbus.App\Assets\icon-256.png'

# --- app.ico: 16/24/32/48 are hand-drawn masters copied as-is; 64/128 are
#     downscaled from the 256 master, 256 from itself ---
$icoPlan = @(
    @{ Size = 16;  From = 16  }, @{ Size = 24;  From = 24  },
    @{ Size = 32;  From = 32  }, @{ Size = 48;  From = 48  },
    @{ Size = 64;  From = 256 }, @{ Size = 128; From = 256 },
    @{ Size = 256; From = 256 })
$images = foreach ($e in $icoPlan) {
    $t = Get-Tile $e.Size $e.From
    if ($e.Size -ge 256) { [byte[]]$b = Get-PngBytes $t } else { [byte[]]$b = Get-BmpEntryBytes $t }
    $t.Dispose()
    @{ Size = $e.Size; Bytes = $b }
}
$ms = New-Object System.IO.MemoryStream
$w  = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]([byte[]]$img.Bytes).Length); $w.Write([uint32]$offset)
    $offset += ([byte[]]$img.Bytes).Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'app.ico'), $ms.ToArray())
$w.Dispose(); $ms.Dispose()
Write-Host ("wrote PgNimbus.App\Assets\app.ico ({0} sizes: {1})" -f $images.Count, (($icoPlan | ForEach-Object { $_.Size }) -join ', '))

# --- MSIX tiles: small tiles (44/50) from the hand-drawn 48 master so the
#     glyph stays crisp; the medium tile (150) from the 256 master ---
foreach ($pair in @(
        @{ Size = 44;  From = 48;  Name = 'Square44x44Logo.png' },
        @{ Size = 50;  From = 48;  Name = 'StoreLogo.png' },
        @{ Size = 150; From = 256; Name = 'Square150x150Logo.png' })) {
    $t = Get-Tile $pair.Size $pair.From
    [System.IO.File]::WriteAllBytes((Join-Path $msixDir $pair.Name), (Get-PngBytes $t))
    $t.Dispose()
    Write-Host "wrote PgNimbus.App\Assets\Msix\$($pair.Name)"
}
