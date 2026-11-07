<#
.SYNOPSIS
    Fails unless every native .exe and .dll under a folder is built for one CPU
    architecture.

.DESCRIPTION
    The release pipeline smoke-launches each Windows build, but that cannot tell
    the architectures apart: Windows on Arm runs x64 programs under emulation,
    so an x64 build would launch and pass on the arm64 runner too. This reads
    the Machine field of each file's PE header instead, so a publish that came
    out x64, or a native library (SkiaSharp, HarfBuzz, ANGLE) restored for the
    wrong RID, fails before it is packaged.

    Managed assemblies (a CLR header in the PE file) are skipped: an IL-only one
    is marked x86 whatever it runs on. A NativeAOT publish holds none.

.PARAMETER Path
    The folder to check (a publish output, an unpacked zip).

.PARAMETER Architecture
    x64 or arm64.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Architecture
)

$ErrorActionPreference = 'Stop'

$machineNames = @{
    0x014C = 'x86'
    0x8664 = 'x64'
    0xAA64 = 'arm64'
    0xA641 = 'arm64ec'
    0x01C4 = 'arm'
}

function Read-PeHeader([string] $File) {
    $bytes = [System.IO.File]::ReadAllBytes($File)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "$File is not a PE file (no MZ header)"
    }
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -le 0 -or $pe + 24 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) {
        throw "$File is not a PE file (no PE signature)"
    }
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)

    # The CLR runtime header is data directory 14. The directories start 96
    # bytes into a PE32 optional header and 112 bytes into a PE32+ one.
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $directories = $optional + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $clr = $directories + 14 * 8
    $managed = $clr + 4 -le $bytes.Length -and [BitConverter]::ToUInt32($bytes, $clr) -ne 0

    [pscustomobject]@{ Machine = $machine; Managed = $managed }
}

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "$Path is not a folder"
}

$files = Get-ChildItem -LiteralPath $Path -Recurse -File -Include *.exe, *.dll
$checked = 0
$wrong = @()
foreach ($file in $files) {
    $header = Read-PeHeader $file.FullName
    if ($header.Managed) { continue }
    $name = $machineNames[[int]$header.Machine] ?? ('0x{0:X4}' -f $header.Machine)
    $relative = [System.IO.Path]::GetRelativePath($Path, $file.FullName)
    Write-Host ("   {0,-8} {1}" -f $name, $relative)
    $checked++
    if ($name -ne $Architecture) { $wrong += "$relative ($name)" }
}

if ($checked -eq 0) { throw "no native .exe or .dll found under $Path" }
if (-not ($files | Where-Object Name -eq 'PgNimbus.App.exe')) {
    throw "PgNimbus.App.exe not found under $Path"
}
if ($wrong) {
    throw "not $Architecture`: $($wrong -join ', ')"
}
Write-Host "ok: $checked native files under $Path are $Architecture"
