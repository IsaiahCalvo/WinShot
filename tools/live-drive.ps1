# WinShot live driver: launch the installed editor on an image, click named
# controls (UIAutomation), drag on the canvas (SendInput), and screenshot the window.
# Usage: pwsh wsdrive.ps1 -Cmd <launch|shot|click|drag|key|type|close> [-Arg ...]
param(
    [string]$Cmd = 'shot',
    [string]$Image = '',
    [string]$Name = '',
    [string]$Out = '',
    [string]$Pts = '',
    [string]$Keys = '',
    [int]$Steps = 24,
    [int]$Delay = 8
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[Win]::SetProcessDPIAware() | Out-Null
# Per-monitor-v2 on this thread so GDI screen copies use physical pixels on every monitor.
[Win]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$P = @(); if ($Pts) { $P = $Pts.Split(',') | ForEach-Object { [double]$_ } }
$exe = "$env:LOCALAPPDATA\Programs\WinShot\WinShot.exe"
$stateFile = "$env:TEMP\wsdrive-hwnd.txt"

function Find-Editor {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $pid_ = $w.Current.ProcessId
        try { $p = Get-Process -Id $pid_ -ErrorAction Stop } catch { continue }
        if ($p.ProcessName -ne 'WinShot') { continue }
        $descCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Done')
        if ($w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $descCond)) { Set-Scale $w; return $w }
    }
    return $null
}

function Find-Named($el, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Panels have no UIA peers, so derive the canvas from chrome that does:
# below the undo button row, left of the rail's zoom-in button, above Done.
function Canvas-Rect($el) {
    $w = Get-Rect $el
    $undo = Get-Rect (Find-Named $el 'Undo')
    $zoomIn = Get-Rect (Find-Named $el 'Zoom in')
    $done = Get-Rect (Find-Named $el 'Done')
    $top = $undo.Y + $undo.H + [int]($undo.H * 0.9)
    $bottom = $done.Y - [int]($done.H * 0.5)
    return @{X=$w.X + 2; Y=$top; W=($zoomIn.X - 12 - $w.X); H=($bottom - $top)}
}

function List-Windows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        try { $p = Get-Process -Id $w.Current.ProcessId -ErrorAction Stop } catch { continue }
        if ($p.ProcessName -eq 'WinShot') { "win '$($w.Current.Name)' $($w.Current.BoundingRectangle)" }
    }
}

# UIA reports DPI-virtualized rects on this thread; scale to physical pixels by the
# editor window's real DPI so cursor moves and screen copies line up.
$script:Scale = 1.0
function Set-Scale($win) {
    # Effective DPI of the monitor the editor sits on; UIA rects on this thread are 96-dpi logical.
    $h = [IntPtr]$win.Current.NativeWindowHandle
    $mon = [Win]::MonitorFromWindow($h, 2)
    $dx = [uint32]0; $dy = [uint32]0
    if ([Win]::GetDpiForMonitor($mon, 0, [ref]$dx, [ref]$dy) -eq 0 -and $dx -gt 0) { $script:Scale = $dx / 96.0 }
}
function Get-Rect($el) {
    $r = $el.Current.BoundingRectangle; $s = $script:Scale
    return @{X=[int]($r.X * $s); Y=[int]($r.Y * $s); W=[int]($r.Width * $s); H=[int]($r.Height * $s)}
}

function Mouse-Move($x, $y) { [Win]::SetCursorPos([int]$x, [int]$y) | Out-Null; Start-Sleep -Milliseconds $Delay }
function Mouse-Down { [Win]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
function Mouse-Up   { [Win]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }

function Shot($el, $path) {
    $r = Get-Rect $el
    $bmp = New-Object System.Drawing.Bitmap $r.W, $r.H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.X, $r.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "shot $path ($($r.W)x$($r.H) at $($r.X),$($r.Y))"
}

switch ($Cmd) {
    'launch' {
        Get-Process WinShot -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        Start-Process $exe -ArgumentList "`"edit:$Image`""
        $el = $null
        for ($i = 0; $i -lt 80 -and -not $el; $i++) { Start-Sleep -Milliseconds 250; $el = Find-Editor }
        if (-not $el) { List-Windows; throw "editor window not found" }
        Start-Sleep -Milliseconds 800
        $r = Get-Rect $el
        "editor at $($r.X),$($r.Y) $($r.W)x$($r.H)"
    }
    'windows' { List-Windows }
    'shot' { $el = Find-Editor; if (-not $el) { throw "no editor" }; Shot $el $Out }
    'click' {
        $el = Find-Editor; if (-not $el) { throw "no editor" }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        $btn = $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $btn) { throw "control '$Name' not found" }
        $r = Get-Rect $btn
        Mouse-Move ($r.X + $r.W / 2) ($r.Y + $r.H / 2); Mouse-Down; Mouse-Up
        Start-Sleep -Milliseconds 250
        "clicked $Name at $($r.X + $r.W/2),$($r.Y + $r.H/2)"
    }
    'canvas' {
        # Print the canvas rect (screen px) so drags can be expressed in canvas fractions.
        $el = Find-Editor; if (-not $el) { throw "no editor" }
        $r = Canvas-Rect $el; "canvas $($r.X),$($r.Y) $($r.W)x$($r.H)"
    }
    'drag' {
        # Pts = x0,y0,x1,y1[,x2,y2...] as fractions (0..1) of the viewport; a polyline drag.
        $el = Find-Editor; if (-not $el) { throw "no editor" }
        $r = Canvas-Rect $el
        $sx = $r.X + $P[0] * $r.W; $sy = $r.Y + $P[1] * $r.H
        Mouse-Move $sx $sy; Mouse-Down
        for ($k = 2; $k -lt $P.Length; $k += 2) {
            $ex = $r.X + $P[$k] * $r.W; $ey = $r.Y + $P[$k+1] * $r.H
            for ($i = 1; $i -le $Steps; $i++) {
                $t = $i / $Steps
                Mouse-Move ($sx + ($ex - $sx) * $t) ($sy + ($ey - $sy) * $t)
            }
            $sx = $ex; $sy = $ey
        }
        Mouse-Up; Start-Sleep -Milliseconds 250
        "dragged $($P.Length / 2) points"
    }
    'move' {
        $el = Find-Editor; if (-not $el) { throw "no editor" }
        $r = Canvas-Rect $el
        Mouse-Move ($r.X + $P[0] * $r.W) ($r.Y + $P[1] * $r.H); Start-Sleep -Milliseconds 400; "moved"
    }
    'key' { [System.Windows.Forms.SendKeys]::SendWait($Keys); Start-Sleep -Milliseconds 250; "sent $Keys" }
    'close' { Get-Process WinShot -ErrorAction SilentlyContinue | Stop-Process -Force; "closed" }
}
