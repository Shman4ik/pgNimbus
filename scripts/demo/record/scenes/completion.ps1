# Completion: FROM + a partial table name, JOIN with the FK-ranked table, ON written in one accept.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme
try {
    Start-App $script:Conn | Out-Null
    Wait-Title '*' ; Pause 3.5; Fit-App; Park-Mouse
    Click 800 260 300; Park-Mouse
    Key 'ctrl+a' 200; Key 'del' 300
    if (-not $Dry) { Start-Rec 'completion' }
    Pause 0.8
    Type-Slow 'SELECT * FROM or' 75
    Pause 1.3
    Key 'enter' 600
    Type-Slow ' ' 100
    Type-Slow 'JOIN ' 75
    Pause 1.4
    Type-Slow 'cus' 90
    Pause 1.0
    Key 'down' 900
    Key 'enter' 1300
    Key 'ctrl+enter' 1500
    Pause 1.6
} finally { Stop-Rec; Stop-App }
