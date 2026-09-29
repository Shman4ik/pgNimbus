# Cold start: a plain dark backdrop stands in for the desktop, then the process launches and the
# main window appears in the capture box. Also prints the probe's own numbers (process start to
# first rendered frame), the figure the README quotes.
param([string]$Theme = 'dark')
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme
$env:PGNIMBUS_CONN = $script:Conn
$env:PGNIMBUS_DATA_DIR = $env:PGN_DEMO_DATA

$times = 1..10 | ForEach-Object {
    $env:PGNIMBUS_STARTUP_PROBE = '1'
    $o = & $script:Exe 2>&1 | Out-String
    if ($o -match 'window_ms=(\d+)') { [int]$Matches[1] }
}
Remove-Item Env:PGNIMBUS_STARTUP_PROBE
"probe window_ms: " + ($times -join ', ') + "  median " + (($times | Sort-Object)[[int]($times.Count / 2)])

# Backdrop: borderless solid window over the capture box.
$bd = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', @"
Add-Type -AssemblyName System.Windows.Forms
`$f = New-Object Windows.Forms.Form
`$f.FormBorderStyle = 'None'; `$f.StartPosition = 'Manual'; `$f.TopMost = `$false
`$f.Location = New-Object Drawing.Point($($script:BX)), $($script:BY)
`$f.Size = New-Object Drawing.Size($($script:BW)), $($script:BH)
`$f.BackColor = [Drawing.Color]::FromArgb(11, 11, 12); `$f.ShowInTaskbar = `$false
[Windows.Forms.Application]::Run(`$f)
"@
Start-Sleep -Milliseconds 1500
try {
    Park-Mouse
    Start-Rec 'cold-start'
    Pause 1.0
    $p = Start-Process $script:Exe -PassThru
    $script:AppPid = $p.Id
    Pause 4.0
    Stop-Rec
} finally {
    Stop-Rec
    Stop-App
    Stop-Process -Id $bd.Id -Force -ErrorAction SilentlyContinue
}
