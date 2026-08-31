# Assembles the shipped app icons from the prepared design masters. Every
# master is rendered from design/logo.svg by scripts/design/make-masters.ps1,
# so run that first if the mark changed. This script does NOT resample a size
# a master already exists at: it copies that master verbatim and only
# downscales the sizes with no master of their own (64, 128, and the MSIX
# scale variants), always from a LARGER master, never upscaling.
#
#   INPUT  design/masters/icon/icon-{16,24,32,48,256,1024}.png   square full-bleed tiles
#          design/masters/window/window-{light,dark}-256.png     transparent line art
#
#   OUTPUT PgNimbus.App/Assets/app.ico                exe + MSI icon (multi-size)
#          PgNimbus.App/Assets/icon-256-light.png     light-theme window icon (transparent)
#          PgNimbus.App/Assets/icon-256-dark.png      dark-theme  window icon (transparent)
#          PgNimbus.App/Assets/Msix/{Square44x44Logo,Square150x150Logo,StoreLogo}
#              .scale-{100,125,150,200,400}.png           MSIX plated tiles, one file per DPI
#          PgNimbus.App/Assets/Msix/Square44x44Logo
#              .targetsize-{16,24,32,48,256}_altform-{unplated,lightunplated}.png
#              transparent taskbar/Alt+Tab/Start icon — without these, Windows adds
#              its own backplate around the plated logo on those surfaces
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

# Alpha-preserving downscale of a transparent master (unlike Get-Tile, which
# only ever reads opaque full-bleed icon masters). Used for the unplated MSIX
# taskbar/Alt+Tab icons, sourced from the transparent window-icon masters.
function Get-TransparentTile([string]$masterPath, [int]$size) {
    $src = New-Object System.Drawing.Bitmap($masterPath)
    if ($src.Width -eq $size -and $src.Height -eq $size) { return $src }
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
# Only app.ico needs this: the Windows shell itself reads that file (Explorer,
# MSI/ARP), and there PNG compression is only spec-blessed for the 256px entry —
# smaller sizes go in as plain BMP for maximum shell compatibility. The
# window-icon .ico files below are all-PNG instead: they're decoded only
# in-app (Avalonia + CreateIconFromResourceEx, both PNG-capable at any size),
# never handed to the shell as a file.
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

# --- per-theme window icons: a real multi-size .ico (16/24/32/48/256, all
#     PNG-compressed entries — Windows Vista+ decodes PNG at any .ico size,
#     so this needs no BMP fallback like app.ico's legacy sizes do) built
#     from the transparent 256px line art. A flat single-size PNG here (the
#     previous approach) leaves Avalonia's Win32 WM_SETICON call with only
#     one oversized image to downscale, which Windows silently fails to
#     apply to the title bar/taskbar on some Windows 11 builds — see
#     ThemedWindowChrome's SendMessage(WM_SETICON) workaround, which needs
#     these multi-size .ico files to pick a correctly-sized HICON from.
$windowIconSizes = 16, 24, 32, 48, 256
foreach ($pair in @(
        @{ Src = 'window-light-256.png'; Dst = 'window-icon-light.ico' },
        @{ Src = 'window-dark-256.png';  Dst = 'window-icon-dark.ico' })) {
    $s = Join-Path $winDir $pair.Src
    if (-not (Test-Path $s)) { throw "Missing window master: $s" }
    $entries = foreach ($size in $windowIconSizes) {
        $t = Get-TransparentTile $s $size
        $bytes = Get-PngBytes $t
        $t.Dispose()
        @{ Size = $size; Bytes = $bytes }
    }
    $ms = New-Object System.IO.MemoryStream
    $w  = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]([byte[]]$e.Bytes).Length); $w.Write([uint32]$offset)
        $offset += ([byte[]]$e.Bytes).Length
    }
    foreach ($e in $entries) { $w.Write([byte[]]$e.Bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $outDir $pair.Dst), $ms.ToArray())
    $w.Dispose(); $ms.Dispose()
    Write-Host ("wrote PgNimbus.App\Assets\$($pair.Dst) ({0} sizes)" -f ($windowIconSizes -join ', '))
}

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

# --- MSIX plated tiles: one file per DPI scale factor (100/125/150/200/400%)
#     for each logo, instead of a single flat file — Windows falls back to
#     scaling (and backplating) a lone unqualified asset when it can't find a
#     qualifier-matched size for the surface it's rendering. Small tiles
#     (Square44x44Logo/StoreLogo) come from the hand-drawn 48 master so the
#     glyph stays crisp; the medium tile (Square150x150Logo) from the 256
#     master — but scale-200/400 can exceed both of those, so anything larger
#     than the logo's small master falls back to the 1024 master instead of
#     upscaling (blurring) it.
$msixScales = @(
    @{ Suffix = 'scale-100'; Factor = 1.0 },
    @{ Suffix = 'scale-125'; Factor = 1.25 },
    @{ Suffix = 'scale-150'; Factor = 1.5 },
    @{ Suffix = 'scale-200'; Factor = 2.0 },
    @{ Suffix = 'scale-400'; Factor = 4.0 })
foreach ($logo in @(
        @{ Base = 44;  SmallFrom = 48;  Name = 'Square44x44Logo' },
        @{ Base = 50;  SmallFrom = 48;  Name = 'StoreLogo' },
        @{ Base = 150; SmallFrom = 256; Name = 'Square150x150Logo' })) {
    foreach ($s in $msixScales) {
        $size = [int][Math]::Round($logo.Base * $s.Factor)
        $from = if ($size -le $logo.SmallFrom) { $logo.SmallFrom } else { 1024 }
        $t = Get-Tile $size $from
        [System.IO.File]::WriteAllBytes((Join-Path $msixDir "$($logo.Name).$($s.Suffix).png"), (Get-PngBytes $t))
        $t.Dispose()
    }
    Write-Host "wrote PgNimbus.App\Assets\Msix\$($logo.Name).scale-{100,125,150,200,400}.png"
}

# --- MSIX unplated Square44x44Logo: transparent taskbar/Alt+Tab/Start icon.
#     Dark-theme (altform-unplated) reuses the light-line window-dark master;
#     light-theme (altform-lightunplated) reuses the dark-line window-light
#     master — both already transparent line art, no new design work needed.
$unplatedSizes = 16, 24, 32, 48, 256
foreach ($pair in @(
        @{ Src = Join-Path $winDir 'window-dark-256.png';  Suffix = 'altform-unplated' },
        @{ Src = Join-Path $winDir 'window-light-256.png'; Suffix = 'altform-lightunplated' })) {
    if (-not (Test-Path $pair.Src)) { throw "Missing window master: $($pair.Src)" }
    foreach ($size in $unplatedSizes) {
        $t = Get-TransparentTile $pair.Src $size
        $name = "Square44x44Logo.targetsize-${size}_$($pair.Suffix).png"
        [System.IO.File]::WriteAllBytes((Join-Path $msixDir $name), (Get-PngBytes $t))
        $t.Dispose()
    }
}
Write-Host "wrote PgNimbus.App\Assets\Msix\Square44x44Logo.targetsize-{16,24,32,48,256}_altform-{unplated,lightunplated}.png"
