# The Microsoft Store trailer: five short takes in a 16:9 box (1600x900, scaled to
# 1920x1080 by make-trailer.sh), each from a fresh app and data folder:
#   trailer-1-start       cold start (the cold-start scene, in this box)
#   trailer-2-palette     Ctrl+K to a table, browse its rows
#   trailer-3-completion  FROM + JOIN ... ON from the foreign key, then Run
#   trailer-4-safe-mode   two staged cell edits, reviewed and committed
#   trailer-5-explain     EXPLAIN ANALYZE as text, then the plan tree and its metrics
# Coordinates are for the 1600x900 box; grab a frame and re-read them if the layout moves.
param([string]$Theme = 'dark', [string[]]$Only)
if (-not $env:PGN_DEMO_BOX) { $env:PGN_DEMO_BOX = '1120,350,1600,900' }
. "$PSScriptRoot\..\demo-lib.ps1"
if ($script:BW -ne 1600 -or $script:BH -ne 900) { throw 'store-trailer.ps1 is laid out for a 1600x900 box.' }
function Want([string]$n) { -not $Only -or $Only -contains $n }

if (Want 'start') {
    & "$PSScriptRoot\cold-start.ps1" -Theme $Theme
    Move-Item -Force (Join-Path $script:Out 'cold-start.mp4') (Join-Path $script:Out 'trailer-1-start.mp4')
}

if (Want 'palette') {
    Prep-Data $Theme
    try {
        Start-App $script:Conn | Out-Null
        Pause 3.5; Fit-App; Park-Mouse
        Click 800 260 300; Park-Mouse
        Start-Rec 'trailer-2-palette'
        Pause 1.0
        Key 'ctrl+k' 900
        Type-Slow 'devices' 110
        Pause 1.2
        Key 'enter' 2600          # iot.devices: uuid, macaddr, inet, cidr, bit, point, box
        Pause 0.8
    } finally { Stop-Rec; Stop-App }
}

if (Want 'completion') {
    Prep-Data $Theme
    try {
        Start-App $script:Conn | Out-Null
        Pause 3.5; Fit-App; Park-Mouse
        Click 800 260 300; Park-Mouse
        Key 'ctrl+a' 200; Key 'del' 300
        Start-Rec 'trailer-3-completion'
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
}

if (Want 'safe-mode') {
    Prep-Data $Theme
    try {
        Start-App $script:Conn | Out-Null
        Pause 3.5; Fit-App; Park-Mouse
        Start-Rec 'trailer-4-safe-mode' -Mouse
        Pause 0.8
        Click 29 248 900                       # expand public
        Click 98 272 1800 -Double              # browse customers
        Click 585 539 700 -Double              # last_name of row 3
        Key 'ctrl+a' 200; Type-Slow 'Taylor-Reid' 70; Key 'enter' 1000
        Click 735 639 700 -Double              # email of row 7
        Key 'ctrl+a' 200; Type-Slow 'liam.anderson@example.com' 45; Key 'enter' 1400
        Click 560 815 900                      # click off the editor onto a row
        Click 1131 866 2600                    # Review...
        Click 1024 659 2600                    # Commit 2 changes
        Park-Mouse
        Pause 1.2
    } finally { Stop-Rec; Stop-App }
}

if (Want 'explain') {
    $sql = @"
SELECT c.last_name, count(*) AS orders, sum(i.quantity * i.unit_price) AS revenue
FROM orders o
JOIN customers c ON c.id = o.customer_id
JOIN order_items i ON i.order_id = o.id
WHERE o.status = 'paid'
GROUP BY c.last_name
ORDER BY revenue DESC
LIMIT 10;
"@
    Prep-Data $Theme
    try {
        Start-App $script:Conn | Out-Null
        Pause 3.5; Fit-App; Park-Mouse
        Set-Clipboard -Value $sql
        Click 800 260 300; Park-Mouse
        Key 'ctrl+a' 200; Key 'ctrl+v' 800
        Drag 956 441 956 330 400     # give the plan more room than the editor
        Click 800 200 300; Park-Mouse
        Key 'ctrl+enter' 1500
        Start-Rec 'trailer-5-explain' -Mouse
        Pause 1.0
        Key 'ctrl+shift+e' 3000      # Explain Analyze: opens as text
        Click 1521 352 2400          # Tree
        Click 443 352 1600           # Color: Rows
        Click 486 352 1600           # Cost
        Click 531 352 1600           # Buffers
        Click 401 352 1800           # back to Time
        Park-Mouse
        Pause 0.8
    } finally { Stop-Rec; Stop-App }
}
