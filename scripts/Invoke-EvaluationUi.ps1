param(
    [Parameter(Mandatory)][ValidateSet('tree','shot','click','invoke','type','key','focus','scroll','setvalue','toggle','select','expand','rightclick','dblclick','drag')][string]$Action,
    [string]$Name, [string]$Id, [string]$Type, [int]$Nth = 0,
    [int]$X = -1, [int]$Y = -1, [int]$X2 = -1, [int]$Y2 = -1,
    [string]$Text, [string]$Keys, [string]$Out, [int]$Depth = 60, [string]$Match,
    [int]$Wheel = 0, [switch]$All, [string]$ProcName = 'GhProjectsBoards.App', [string]$Under, [int]$ProcId = 0
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
function Dec($v) { if ($v -and $v.StartsWith('b64:')) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($v.Substring(4))) } else { $v } }
$Name = Dec $Name; $Match = Dec $Match; $Text = Dec $Text; $Under = Dec $Under; $Keys = Dec $Keys; $Id = Dec $Id
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[W]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

if ($ProcId -eq 0 -and $env:GHPB_EVAL_PID) { $ProcId = [int]$env:GHPB_EVAL_PID }
if ($ProcId -ne 0) { $proc = Get-Process -Id $ProcId } else { $proc = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
if (-not $proc) { throw "No window for $ProcName" }
$hwnd = $proc.MainWindowHandle
$root = $A::FromHandle($hwnd)

function Front {
    if ([W]::IsIconic($hwnd)) { [W]::ShowWindow($hwnd, 9) | Out-Null }
    [W]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [W]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
    [W]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 150
}
function WinRect { $r = New-Object W+RECT; [W]::GetWindowRect($hwnd, [ref]$r) | Out-Null; $r }

# Search all top-level windows of the process too (popups / dialogs are often separate HWNDs)
function Roots {
    $list = @($root)
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $proc.Id)
    foreach ($w in $A::RootElement.FindAll($TS::Children, $cond)) { if (-not [System.Windows.Automation.Automation]::Compare($w, $root)) { $list += $w } }
    $list
}

function Find-Target {
    $found = @()
    foreach ($r in Roots) {
        $base = $r
        if ($Under) {
            $u = $r.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Under)))
            if (-not $u) { continue }; $base = $u
        }
        foreach ($e in $base.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            try {
                $c = $e.Current
                if ($Name -and $c.Name -ne $Name) { continue }
                if ($Match -and $c.Name -notmatch $Match) { continue }
                if ($Id -and $c.AutomationId -ne $Id) { continue }
                if ($Type -and $c.ControlType.ProgrammaticName -ne "ControlType.$Type") { continue }
                if ($c.IsOffscreen -and -not $All) { continue }
                $found += $e
            } catch {}
        }
    }
    if ($found.Count -le $Nth) { throw "Not found: name='$Name' match='$Match' id='$Id' type='$Type' nth=$Nth (count=$($found.Count))" }
    $found[$Nth]
}

function Center($e) { $b = $e.Current.BoundingRectangle; [int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2) }
function Click([int]$sx, [int]$sy, [uint32]$down = 2, [uint32]$up = 4) {
    [W]::SetCursorPos($sx, $sy) | Out-Null; Start-Sleep -Milliseconds 60
    [W]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 40
    [W]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero)
}
function AbsPoint {
    $r = WinRect
    if ($X -ge 0) { return ($r.L + $X), ($r.T + $Y) }
    Center (Find-Target)
}

switch ($Action) {
    'tree' {
        $i = 0
        foreach ($r in Roots) {
            $wr = WinRect
            Write-Output ("# root: " + $r.Current.Name + " [" + $r.Current.ControlType.ProgrammaticName + "] win=" + $wr.L + "," + $wr.T + "," + ($wr.R - $wr.L) + "x" + ($wr.B - $wr.T))
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $stack = New-Object System.Collections.Stack
            $stack.Push(@($r, 0))
            while ($stack.Count -gt 0) {
                $item = $stack.Pop(); $e = $item[0]; $d = $item[1]
                try { $c = $e.Current } catch { continue }
                if ($c.IsOffscreen -and -not $All -and $d -gt 0) { continue }
                try {
                $b = $c.BoundingRectangle
                $rel = if ($b.IsEmpty) { '' } else { "@" + [int]($b.X - $wr.L) + "," + [int]($b.Y - $wr.T) + " " + [int]$b.Width + "x" + [int]$b.Height }
                $val = ''
                try { $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); if ($vp.Current.Value) { $val = " val='" + $vp.Current.Value + "'" } } catch {}
                $en = if ($c.IsEnabled) { '' } else { ' (disabled)' }
                $line = ('  ' * $d) + $c.ControlType.ProgrammaticName.Replace('ControlType.', '') + " '" + $c.Name + "'" + $(if ($c.AutomationId) { " #" + $c.AutomationId } else { '' }) + $val + $en + " " + $rel
                if (-not $Match -or $line -match $Match) { Write-Output $line }
                } catch {}
                if ($d -lt $Depth) {
                    $kids = @(); try { $k = $walker.GetFirstChild($e); while ($k) { $kids += $k; $k = $walker.GetNextSibling($k) } } catch {}
                    for ($j = $kids.Count - 1; $j -ge 0; $j--) { $stack.Push(@($kids[$j], $d + 1)) }
                }
            }
        }
    }
    'shot' {
        $r = WinRect
        $w = $r.R - $r.L; $h = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc(); [W]::PrintWindow($hwnd, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "saved $Out ${w}x${h}"
    }
    'click' { Front; $p = AbsPoint; Click $p[0] $p[1]; Write-Output "clicked $($p[0]),$($p[1])" }
    'rightclick' { Front; $p = AbsPoint; Click $p[0] $p[1] 8 16; Write-Output "rclicked" }
    'dblclick' { Front; $p = AbsPoint; Click $p[0] $p[1]; Start-Sleep -Milliseconds 60; Click $p[0] $p[1]; Write-Output "dblclicked" }
    'drag' {
        Front; $r = WinRect
        [W]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null; Start-Sleep -Milliseconds 100
        [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 100
        for ($s = 1; $s -le 15; $s++) { [W]::SetCursorPos($r.L + $X + [int](($X2 - $X) * $s / 15), $r.T + $Y + [int](($Y2 - $Y) * $s / 15)) | Out-Null; Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 150
        [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero); Write-Output "dragged"
    }
    'invoke' { $e = Find-Target; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output "invoked $($e.Current.Name)" }
    'toggle' { $e = Find-Target; $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Write-Output "toggled" }
    'select' { $e = Find-Target; $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Output "selected $($e.Current.Name)" }
    'expand' { $e = Find-Target; $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Write-Output "expanded" }
    'setvalue' { $e = Find-Target; $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text); Write-Output "set" }
    'focus' { Front; $e = Find-Target; $e.SetFocus(); Write-Output "focused $($e.Current.Name)" }
    'type' { Front; [System.Windows.Forms.SendKeys]::SendWait($Text); Write-Output "typed" }
    'key' { Front; [System.Windows.Forms.SendKeys]::SendWait($Keys); Write-Output "keys $Keys" }
    'scroll' { Front; $p = AbsPoint; [W]::SetCursorPos($p[0], $p[1]) | Out-Null; Start-Sleep -Milliseconds 50; [W]::mouse_event(0x800, 0, 0, $Wheel, [IntPtr]::Zero); Write-Output "wheel $Wheel" }
}
