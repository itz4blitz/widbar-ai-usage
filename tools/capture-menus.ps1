# Opens each widget's flyout (and its settings page), scrolls through it and
# saves cropped screenshots to artifacts\menus. Used to review the menus' look.
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class Input {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint x, uint y, int d, UIntPtr e);
    public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(200); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
    public static void Wheel(int x, int y, int notches) { SetCursorPos(x, y); System.Threading.Thread.Sleep(100); mouse_event(0x800,0,0,-120*notches,UIntPtr.Zero); }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    // Largest visible window of a process that sits above the taskbar: the flyout.
    public static int[] FindFlyout(int[] pids) {
        RECT best = new RECT(); int area = 0;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!IsWindowVisible(h) || Array.IndexOf(pids, (int)pid) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            int a = (r.R - r.L) * (r.B - r.T);
            if (r.B < 1035 && r.B - r.T > 150 && a > area) { area = a; best = r; }
            return true;
        }, IntPtr.Zero);
        return area == 0 ? null : new[] { best.L, best.T, best.R, best.B };
    }
}
'@
$out = Join-Path $PSScriptRoot '..\artifacts\menus'
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter "$Only*.png" | ForEach-Object { $_.Delete() }

function Shot([int[]]$r, [string]$name) {
    $w = $r[2] - $r[0]; $h = $r[3] - $r[1]
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($r[0], $r[1], 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $out "$name.png")); $bmp.Dispose()
}
function Close-Flyout { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 400; [Input]::Click(1500, 300); Start-Sleep -Milliseconds 800 }

function Capture([string]$name, [int]$tileX, [bool]$settings) {
    $pids = @(Get-Process WidBar, WidBar.AiUsage -ErrorAction SilentlyContinue | ForEach-Object Id)
    [Input]::Click($tileX, 1045); Start-Sleep -Seconds 3
    $r = [Input]::FindFlyout($pids)
    if (-not $r) { Write-Warning "$name flyout didn't open"; Close-Flyout; return }
    if ($settings) {
        # The gear sits in the flyout's top-right corner.
        [Input]::Click($r[2] - 26, $r[1] + 22); Start-Sleep -Seconds 2
        $r = [Input]::FindFlyout($pids); $name += '-settings'
    }
    $cx = [int](($r[0] + $r[2]) / 2); $cy = [int](($r[1] + $r[3]) / 2)
    # The flyout keeps its scroll position between openings; start at the top.
    [Input]::Wheel($cx, $cy, -40); Start-Sleep -Milliseconds 700
    for ($page = 1; $page -le 6; $page++) {
        Shot $r "$name-$page"
        [Input]::Wheel($cx, $cy, 5); Start-Sleep -Milliseconds 700
        $prev = Join-Path $out "$name-$page.png"
        Shot $r "$name-probe"
        $same = (Get-FileHash $prev).Hash -eq (Get-FileHash (Join-Path $out "$name-probe.png")).Hash
        Remove-Item (Join-Path $out "$name-probe.png")
        if ($same) { break }
    }
    # Settings open as a separate dialog; Cancel closes it without saving.
    if ($settings) { [Input]::Click($r[2] - 92, $r[3] - 44); Start-Sleep -Seconds 1 }
    Close-Flyout
    "{0}: {1} ({2}x{3})" -f $name, ($r -join ','), ($r[2] - $r[0]), ($r[3] - $r[1])
}

if (-not $Only -or $Only -eq 'system') { Capture 'system' 600 $false; Capture 'system' 600 $true }
if (-not $Only -or $Only -eq 'ai') { Capture 'ai' 1100 $false; Capture 'ai' 1100 $true }
[Input]::SetCursorPos(1500, 300) | Out-Null
