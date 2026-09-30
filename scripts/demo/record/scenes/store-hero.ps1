# The window behind the Store's 16:9 Super hero art: the everyday case, a query and its rows,
# dark theme, captured from the real app on Windows (1600x900 box, like the trailer).
# Writes store-hero-source.png; copy it to design/store/SuperHeroArt-source.dark.png and run
# scripts/windows/make-store-hero.ps1.
param([string]$Theme = 'dark')
if (-not $env:PGN_DEMO_BOX) { $env:PGN_DEMO_BOX = '1120,350,1600,900' }
. "$PSScriptRoot\..\demo-lib.ps1"
if ($script:BW -ne 1600 -or $script:BH -ne 900) { throw 'store-hero.ps1 is laid out for a 1600x900 box.' }
$sql = @"
SELECT o.id, c.first_name, c.last_name, c.email, o.status, o.total_amount, o.order_date
FROM orders o
JOIN customers c ON c.id = o.customer_id
WHERE o.status <> 'cancelled'
ORDER BY o.order_date DESC
LIMIT 200;
"@
Prep-Data $Theme
try {
    Start-App $script:Conn | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    Click 29 248 900                 # expand public
    Set-Clipboard -Value $sql
    Click 800 260 300
    Key 'ctrl+a' 200; Key 'ctrl+v' 600
    Drag 956 441 956 312 400         # six lines of SQL need little room; give it to the rows
    Click 800 200 300
    Key 'ctrl+enter' 2000
    Click 700 437 600                # select a row near the top of the grid
    Park-Mouse
    Pause 0.6
    Grab 'store-hero-source'
} finally { Stop-App }
