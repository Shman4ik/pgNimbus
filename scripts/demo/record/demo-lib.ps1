# Scripted demo recording for pgNimbus: window placement, ffmpeg region capture,
# and keyboard/mouse input that is only ever sent while pgNimbus is in front.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Demo {
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int hh,bool r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk,byte scan,uint f,UIntPtr e);
 [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code,uint type);
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
 [StructLayout(LayoutKind.Sequential)] public struct POINT{public int X,Y;}
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool f);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 public static void Front(IntPtr h){ uint p; IntPtr fg=GetForegroundWindow(); uint ft=GetWindowThreadProcessId(fg,out p); uint me=GetCurrentThreadId(); AttachThreadInput(me,ft,true); ShowWindow(h,9); SetForegroundWindow(h); AttachThreadInput(me,ft,false);}
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
}
"@
Add-Type -AssemblyName System.Windows.Forms
[Demo]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$script:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$script:Exe = if ($env:PGN_DEMO_EXE) { $env:PGN_DEMO_EXE } else { Join-Path $script:Repo 'artifacts\aot\PgNimbus.App.exe' }
$script:FF = (Get-Command ffmpeg -ErrorAction Stop).Source
$script:Out = if ($env:PGN_DEMO_OUT) { $env:PGN_DEMO_OUT } else { Join-Path $PSScriptRoot 'out\raw' }
New-Item -ItemType Directory -Force $script:Out | Out-Null
# The capture box: centered on the 3840x1600 desktop, well inside the edges.
# PGN_DEMO_BOX="x,y,w,h" overrides it (the Store trailer records a 16:9 box).
$script:BX = 1208; $script:BY = 350; $script:BW = 1424; $script:BH = 892
if ($env:PGN_DEMO_BOX) { $script:BX, $script:BY, $script:BW, $script:BH = $env:PGN_DEMO_BOX.Split(',') | ForEach-Object { [int]$_ } }
$script:AppPid = 0

function Start-App([string]$Conn) {
    # Never the real profiles: the data dir must be an explicit scratch folder.
    if (-not $env:PGN_DEMO_DATA) { throw 'Set PGN_DEMO_DATA to an empty scratch folder first.' }
    $env:PGNIMBUS_DATA_DIR = $env:PGN_DEMO_DATA
    if ($Conn) { $env:PGNIMBUS_CONN = $Conn } else { Remove-Item Env:PGNIMBUS_CONN -ErrorAction SilentlyContinue }
    $p = Start-Process $script:Exe -PassThru
    $script:AppPid = $p.Id
    for ($i = 0; $i -lt 100; $i++) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 50 }
    Fit-App
    return $p
}

function Get-AppWindow { (Get-Process -Id $script:AppPid).MainWindowHandle }

function Fit-App {
    $h = Get-AppWindow
    if ($h -eq 0) { return }
    [Demo]::MoveWindow($h, $script:BX - 8, $script:BY, $script:BW + 16, $script:BH + 8, $true) | Out-Null
    [Demo]::Front($h)
}

function Get-FrontPid { $fg = [Demo]::GetForegroundWindow(); $q = 0; [Demo]::GetWindowThreadProcessId($fg, [ref]$q) | Out-Null; return $q }

function Assert-Front {
    for ($i = 0; $i -lt 6; $i++) {
        if ((Get-FrontPid) -eq $script:AppPid) { return }
        [Demo]::Front((Get-AppWindow)); Start-Sleep -Milliseconds 250
    }
    $q = Get-FrontPid
    $n = (Get-Process -Id $q -ErrorAction SilentlyContinue).ProcessName
    throw "pgNimbus is not in front (pid $q $n); stopping input."
}

function Esc-SendKeys([string]$c) {
    if ('+^%~(){}[]'.Contains($c)) { return '{' + $c + '}' }
    return $c
}

# Types text one character at a time, the way a person would.
function Type-Slow([string]$Text, [int]$Ms = 70) {
    foreach ($ch in $Text.ToCharArray()) {
        Assert-Front
        if ($ch -eq "`n") { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
        else { [System.Windows.Forms.SendKeys]::SendWait((Esc-SendKeys ([string]$ch))) }
        Start-Sleep -Milliseconds ($Ms + (Get-Random -Minimum -20 -Maximum 25))
    }
}

# A chord as real key events: 'ctrl+k', 'ctrl+shift+e', 'enter', 'down', 'f1', 'ctrl+v'.
# SendKeys can't be used for these: Avalonia never sees its Ctrl.
$script:Vk = @{
    ctrl = 0x11; shift = 0x10; alt = 0x12; enter = 0x0D; esc = 0x1B; tab = 0x09; space = 0x20
    back = 0x08; del = 0x2E; home = 0x24; end = 0x23; pgup = 0x21; pgdn = 0x22
    left = 0x25; up = 0x26; right = 0x27; down = 0x28; comma = 0xBC; slash = 0xBF
}
function Ext([int]$c) { if (@(0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x2E) -contains $c) { 1 } else { 0 } }
function Key([string]$Chord, [int]$After = 300, [int]$Repeat = 1) {
    $codes = foreach ($k in $Chord.ToLower().Split('+')) {
        if ($script:Vk.ContainsKey($k)) { $script:Vk[$k] }
        elseif ($k -match '^f(\d+)$') { 0x6F + [int]$Matches[1] }
        elseif ($k.Length -eq 1) { [int][char]$k.ToUpper() }
        else { throw "Unknown key '$k'" }
    }
    for ($r = 0; $r -lt $Repeat; $r++) {
        Assert-Front
        foreach ($c in $codes) { [Demo]::keybd_event([byte]$c, [byte][Demo]::MapVirtualKey($c, 0), (Ext $c), [UIntPtr]::Zero); Start-Sleep -Milliseconds 25 }
        [array]::Reverse($codes)
        foreach ($c in $codes) { [Demo]::keybd_event([byte]$c, [byte][Demo]::MapVirtualKey($c, 0), (2 -bor (Ext $c)), [UIntPtr]::Zero); Start-Sleep -Milliseconds 15 }
        [array]::Reverse($codes)
        if ($Repeat -gt 1) { Start-Sleep -Milliseconds 120 }
    }
    Start-Sleep -Milliseconds $After
}

function Pause([double]$Seconds) { Start-Sleep -Milliseconds ([int]($Seconds * 1000)) }

# Coordinates are relative to the capture box's top-left.
function Move-To([int]$X, [int]$Y, [int]$Ms = 450) {
    $p = New-Object Demo+POINT; [Demo]::GetCursorPos([ref]$p) | Out-Null
    $tx = $script:BX + $X; $ty = $script:BY + $Y
    $steps = [Math]::Max(8, [int]($Ms / 15))
    for ($i = 1; $i -le $steps; $i++) {
        $t = $i / $steps; $e = 1 - [Math]::Pow(1 - $t, 3)
        [Demo]::SetCursorPos([int]($p.X + ($tx - $p.X) * $e), [int]($p.Y + ($ty - $p.Y) * $e)) | Out-Null
        Start-Sleep -Milliseconds 15
    }
}

function Click([int]$X, [int]$Y, [int]$After = 400, [switch]$Right, [switch]$Double) {
    Move-To $X $Y
    Assert-Front
    $down = 0x2; $up = 0x4
    if ($Right) { $down = 0x8; $up = 0x10 }
    [Demo]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
    [Demo]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
    if ($Double) { Start-Sleep -Milliseconds 60; [Demo]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40; [Demo]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds $After
}

function Drag([int]$X1, [int]$Y1, [int]$X2, [int]$Y2, [int]$After = 400) {
    Move-To $X1 $Y1
    Assert-Front
    [Demo]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
    Move-To $X2 $Y2 500
    [Demo]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $After
}

function Park-Mouse { [Demo]::SetCursorPos($script:BX + $script:BW + 300, $script:BY + 200) | Out-Null }

function Scroll([int]$X, [int]$Y, [int]$Clicks, [int]$Ms = 60) {
    Move-To $X $Y 300
    $d = if ($Clicks -lt 0) { [uint32]120 } else { [uint32]([uint32]::MaxValue - 119) }
    for ($i = 0; $i -lt [Math]::Abs($Clicks); $i++) { Assert-Front; [Demo]::mouse_event(0x800, 0, 0, $d, [UIntPtr]::Zero); Start-Sleep -Milliseconds $Ms }
}

$script:Rec = $null
function Start-Rec([string]$Name, [switch]$Mouse) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:FF
    $dm = if ($Mouse) { 1 } else { 0 }
    $psi.Arguments = "-loglevel error -y -f gdigrab -framerate 30 -draw_mouse $dm -offset_x $($script:BX) -offset_y $($script:BY) -video_size $($script:BW)x$($script:BH) -i desktop -c:v libx264 -preset ultrafast -crf 16 -pix_fmt yuv420p `"$($script:Out)\$Name.mp4`""
    $psi.RedirectStandardInput = $true; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $script:Rec = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Milliseconds 700
}

function Stop-Rec {
    if ($script:Rec) { $script:Rec.StandardInput.Write('q'); $script:Rec.StandardInput.Flush(); $script:Rec.WaitForExit(15000) | Out-Null; $script:Rec = $null }
}

function Stop-App { if ($script:AppPid) { Stop-Process -Id $script:AppPid -Force -ErrorAction SilentlyContinue; $script:AppPid = 0 } }

function Grab([string]$Name) {
    & $script:FF -loglevel error -y -f gdigrab -offset_x $script:BX -offset_y $script:BY -video_size "$($script:BW)x$($script:BH)" -i desktop -frames:v 1 -update 1 "$($script:Out)\$Name.png"
}




# A fresh copy of the base data dir (one saved profile, "Local Dev") per scene, so no scene
# inherits the previous one's tabs or history. PGN_DEMO_BASE is the pristine copy.
$script:Conn = 'Host=127.0.0.1;Port=5443;Database=demo;Username=postgres;Password=postgres'
function Prep-Data([string]$Theme = 'dark', [hashtable]$Settings = @{}) {
    if (-not $env:PGN_DEMO_BASE) { throw 'Set PGN_DEMO_BASE to the pristine data dir.' }
    $run = Join-Path (Split-Path $env:PGN_DEMO_BASE) 'run'
    if (Test-Path $run) { Remove-Item $run -Recurse -Force }
    Copy-Item $env:PGN_DEMO_BASE $run -Recurse
    $f = Join-Path $run 'settings.json'
    $j = Get-Content $f -Raw | ConvertFrom-Json
    $j.Theme = $Theme
    foreach ($k in $Settings.Keys) { $j | Add-Member -NotePropertyName $k -NotePropertyValue $Settings[$k] -Force }
    $j | ConvertTo-Json -Depth 6 | Set-Content $f -Encoding utf8
    Copy-Item (Join-Path $run 'connection-window.json') (Join-Path $run 'window.json')   # the main window opens in the capture box
    $env:PGN_DEMO_DATA = $run
}

function Wait-Title([string]$Like, [int]$TimeoutMs = 15000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ((Get-Process -Id $script:AppPid).MainWindowTitle -like $Like) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Window title never matched '$Like'."
}
