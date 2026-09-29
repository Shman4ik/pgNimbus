# Explain: EXPLAIN ANALYZE as text, then the plan tree with heat bars, and the Color switch.
param([string]$Theme = 'dark', [switch]$Dry)
. "$PSScriptRoot\..\demo-lib.ps1"
Prep-Data $Theme
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
try {
    Start-App $script:Conn | Out-Null
    Pause 3.5; Fit-App; Park-Mouse
    Set-Clipboard -Value $sql
    Click 800 260 300; Park-Mouse
    Key 'ctrl+a' 200; Key 'ctrl+v' 800
    Drag 868 438 868 330 400     # give the plan more room than the editor
    Click 800 200 300; Park-Mouse
    Key 'ctrl+enter' 1500        # a normal run first, so the results pane has a result to leave
    if (-not $Dry) { Start-Rec 'explain-tree' -Mouse }
    Pause 1.0
    Key 'ctrl+shift+e' 3000      # Explain Analyze: opens as text
    Click 1345 352 2200          # Tree
    Click 443 352 1600           # Color: Rows
    Click 486 352 1600           # Cost
    Click 531 352 1600           # Buffers
    Click 401 352 1800           # back to Time
    Park-Mouse
    Pause 0.8
} finally { Stop-Rec; Stop-App }
