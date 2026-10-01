# Packs a dotnet-publish win-x64 output into the portable .zip that ships on
# the GitHub Release: one top-level folder named after the package
# (pgNimbus-<version>-win-x64\PgNimbus.App.exe, …), like the Linux .tar.gz.
# Nothing is installed: unzip anywhere and run PgNimbus.App.exe. Settings live
# in %APPDATA%\pgNimbus either way, so replacing the folder keeps them.
#
# Usage:
#   pwsh scripts/windows/build-zip.ps1 -PublishDir publish\win-x64 -Version 1.2.3 -Output pgNimbus-1.2.3-win-x64.zip
param(
    [Parameter(Mandatory)] [string]$PublishDir,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Output
)
$ErrorActionPreference = 'Stop'

$name = "pgNimbus-$Version-win-x64"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pgnimbus-zip-" + [guid]::NewGuid())
try {
    $root = Join-Path $stage $name
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    # .pdb debug symbols are dev-only and add tens of MB (libSkiaSharp.pdb)
    # with no end-user benefit; the MSIX drops them the same way.
    Copy-Item "$PublishDir\*" -Destination $root -Recurse -Force -Exclude '*.pdb'
    Get-ChildItem -Path $root -Filter '*.pdb' -Recurse | Remove-Item -Force

    if (-not (Test-Path (Join-Path $root 'PgNimbus.App.exe'))) {
        throw "PgNimbus.App.exe not found in $PublishDir"
    }

    if (Test-Path $Output) { Remove-Item $Output -Force }
    Compress-Archive -Path $root -DestinationPath $Output -CompressionLevel Optimal
    Write-Host "Packed $((Get-Item $Output).Length / 1MB -as [int]) MB into $Output"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
