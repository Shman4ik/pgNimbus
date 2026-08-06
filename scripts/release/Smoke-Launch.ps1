<#
.SYNOPSIS
    Launches a built pgNimbus on Windows and asserts it reaches a rendered window.

.DESCRIPTION
    The Windows half of scripts/release/smoke-launch.sh — see that file for why
    this gate exists. Same contract: PGNIMBUS_STARTUP_PROBE=1 makes the app print
    one line once its first window has drawn its first frame, then exit.

    PgNimbus.App is a WinExe, so it has no console of its own; the probe line is
    still readable because redirecting stdout gives the process a handle to write
    to. Both the exit code and the line are asserted — an app that quit before
    drawing anything would also exit 0.

.PARAMETER Label
    What is being smoked, for the log ("publish output", "installed MSI").

.PARAMETER Executable
    Path to PgNimbus.App.exe.

.PARAMETER TimeoutSeconds
    How long to wait for the window before calling it a hang.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Label,
    [Parameter(Mandatory)] [string] $Executable,
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Executable)) {
    throw "smoke ($Label): $Executable does not exist"
}

Write-Host "== smoke: $Label"
Write-Host "   $Executable"

$stdout = New-TemporaryFile
$stderr = New-TemporaryFile

try {
    $env:PGNIMBUS_STARTUP_PROBE = '1'

    $process = Start-Process -FilePath $Executable `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru -NoNewWindow

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        throw "smoke ($Label): no window after ${TimeoutSeconds}s"
    }

    $out = (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue) ?? ''
    $err = (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue) ?? ''
    if ($out) { $out.TrimEnd() -split "`n" | ForEach-Object { Write-Host "   | $_" } }
    if ($err) { $err.TrimEnd() -split "`n" | ForEach-Object { Write-Host "   | $_" } }

    if ($process.ExitCode -ne 0) {
        throw "smoke ($Label): exited with status $($process.ExitCode)"
    }

    $probe = ($out -split "`r?`n") | Where-Object { $_ -match 'PGNIMBUS_STARTUP_PROBE' } | Select-Object -First 1
    if (-not $probe) {
        throw "smoke ($Label): exited cleanly but never rendered a window"
    }

    Write-Host "   ok: $($probe.Trim())"
}
finally {
    $env:PGNIMBUS_STARTUP_PROBE = $null
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
}
