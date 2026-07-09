# Regenerates the shipped app icons from the design sources:
#   design/icon-tile.png  ->  PgNimbus.App/Assets/icon-256.png        (macOS .icns source)
#                         ->  PgNimbus.App/Assets/app.ico             (exe + MSI icon)
#                         ->  PgNimbus.App/Assets/Msix/*.png          (MSIX package tile assets)
#   design/logo-light.png ->  PgNimbus.App/Assets/icon-256-light.png  (window icon, light theme)
#   design/logo-dark.png  ->  PgNimbus.App/Assets/icon-256-dark.png   (window icon, dark theme)
#
# Windows-only (uses System.Drawing/GDI+). Run after changing the design PNGs:
#   pwsh scripts/windows/make-app-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$src  = New-Object System.Drawing.Bitmap((Join-Path $repo 'design\icon-tile.png'))

# Rounded-corner tile at a given size, rendered fresh from the full-res source
# so corners stay crisp at every ICO entry size.
function New-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    $r = [Math]::Max(1, [Math]::Round($size * 0.22))  # Windows-11-style corner ratio
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

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
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
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

# --- per-theme window icons: plain 256px resizes of the transparent line art ---
foreach ($pair in @(
        @{ Src = 'logo-light.png'; Dst = 'icon-256-light.png' },
        @{ Src = 'logo-dark.png';  Dst = 'icon-256-dark.png' })) {
    $art = New-Object System.Drawing.Bitmap((Join-Path $repo "design\$($pair.Src)"))
    $bmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.DrawImage($art, 0, 0, 256, 256)
    $g.Dispose()
    [System.IO.File]::WriteAllBytes((Join-Path $repo "PgNimbus.App\Assets\$($pair.Dst)"), (Get-PngBytes $bmp))
    $bmp.Dispose(); $art.Dispose()
    Write-Host "wrote PgNimbus.App\Assets\$($pair.Dst)"
}

# --- icon-256.png (macOS icns source) ---
$tile256 = New-Tile 256
[System.IO.File]::WriteAllBytes((Join-Path $repo 'PgNimbus.App\Assets\icon-256.png'), (Get-PngBytes $tile256))
$tile256.Dispose()
Write-Host 'wrote PgNimbus.App\Assets\icon-256.png'

# --- app.ico (exe + MSI icon): BMP entries up to 128, PNG for 256 ---
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $t = New-Tile $s
    if ($s -ge 256) { [byte[]]$b = Get-PngBytes $t } else { [byte[]]$b = Get-BmpEntryBytes $t }
    $t.Dispose()
    @{ Size = $s; Bytes = $b }
}

$ms = New-Object System.IO.MemoryStream
$w  = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type: icon
$w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim)            # width  (0 = 256)
    $w.Write([byte]$dim)            # height
    $w.Write([byte]0)               # palette colors
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # color planes
    $w.Write([uint16]32)            # bits per pixel
    $w.Write([uint32]([byte[]]$img.Bytes).Length)
    $w.Write([uint32]$offset)
    $offset += ([byte[]]$img.Bytes).Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $repo 'PgNimbus.App\Assets\app.ico'), $ms.ToArray())
$w.Dispose(); $ms.Dispose()
Write-Host ("wrote PgNimbus.App\Assets\app.ico ({0} sizes: {1})" -f $images.Count, ($sizes -join ', '))

# --- MSIX package tile assets: the two sizes uap:VisualElements needs plus
#     the Properties/Logo (StoreLogo) the manifest schema requires ---
$msixDir = Join-Path $repo 'PgNimbus.App\Assets\Msix'
New-Item -ItemType Directory -Force -Path $msixDir | Out-Null
foreach ($pair in @(
        @{ Size = 44;  Name = 'Square44x44Logo.png' },
        @{ Size = 150; Name = 'Square150x150Logo.png' },
        @{ Size = 50;  Name = 'StoreLogo.png' })) {
    $tile = New-Tile $pair.Size
    [System.IO.File]::WriteAllBytes((Join-Path $msixDir $pair.Name), (Get-PngBytes $tile))
    $tile.Dispose()
    Write-Host "wrote PgNimbus.App\Assets\Msix\$($pair.Name)"
}
$src.Dispose()
