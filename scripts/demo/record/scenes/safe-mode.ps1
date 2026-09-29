# Safe mode: two cell edits are staged (amber rows), reviewed as SQL, and committed as one transaction.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme
try {
    Start-App $script:Conn | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    if (-not $Dry) { Start-Rec 'safe-mode' -Mouse }
    Pause 0.8
    Click 29 248 900                       # expand public
    Click 98 272 1800 -Double              # browse customers
    Click 585 539 700 -Double              # last_name of row 3
    Key 'ctrl+a' 200; Type-Slow 'Taylor-Reid' 70; Key 'enter' 1000
    Click 735 639 700 -Double              # email of row 7
    Key 'ctrl+a' 200; Type-Slow 'liam.anderson@example.com' 45; Key 'enter' 1400
    Click 560 815 900                      # click off the editor onto a row
    Click 955 858 2400                     # Review...
    Click 936 655 2600                     # Commit 2 changes
    Park-Mouse
    Pause 1.2
} finally { Stop-Rec; Stop-App }
