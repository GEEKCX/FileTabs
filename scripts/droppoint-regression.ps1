<#
DropPoint regression (PRD acceptance #1 + #10 reuse path + render smoke).

Drives a running (or freshly started) FileTabs instance: sends the global
hot-key Shift+CapsLock four times, then asserts against %APPDATA%\FileTabs\app.log:
  - the 1st press creates a pad at slot 0 (right-center base position)
  - presses 2-4 REUSE the empty pad instead of stacking new ones (PRD #10:
    empty pads are reused; slots 1/2 and the 3-pad limit only appear when
    pads hold files, which requires a human OLE drag — see NOTES.md)
Captures the pad window to PNG so visuals can be eyeballed.

Not automatable here (needs a human): drag-in/drag-out through OLE (#2/#3/#5),
stair offsets with 2-3 occupied pads (#11 full path), settings toggle (#7),
theme switch (#8), remembered position (#12).

Usage: powershell -File scripts\droppoint-regression.ps1
Exit code 0 = all checks passed. CapsLock state is restored afterwards.
#>
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class DropPointNative {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern short GetKeyState(byte vk);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    public struct RECT { public int L, T, R, B; }

    // All top-level visible windows of a process whose title contains "拖放中转"
    // (a pad is WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW, so it never shows in
    // EnumWindows' usual first-match helpers).
    public static List<IntPtr> FindPadWindows(uint pid) {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(128);
            GetWindowTextW(h, sb, 128);
            if (sb.ToString().Contains("拖放中转")) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // WM_CLOSE: a pad with staged files treats close as dismiss, which is
    // exactly what we want; empty pads just disappear. Never force-kill.
    public static int CloseAllPads(uint pid) {
        var pads = FindPadWindows(pid);
        foreach (var h in pads) PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
        return pads.Count;
    }
}
"@

$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$check, [string]$detail) {
    $failures.Add("$check : $detail") | Out-Null
    Write-Host "  FAIL $check - $detail" -ForegroundColor Red
}
function Pass([string]$check, [string]$detail) {
    Write-Host "  PASS $check" -ForegroundColor Green
    if ($detail) { Write-Host "       $detail" -ForegroundColor DarkGray }
}

# ---------- 0. instance ----------
$proc = Get-Process FileTabs -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending | Select-Object -First 1
if (-not $proc) {
    Write-Host "FileTabs not running; starting Debug build..."
    $exe = Join-Path $PSScriptRoot "..\bin\Debug\net10.0-windows\FileTabs.exe"
    if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot "..\bin\Release\net10.0-windows\win-x64\publish\FileTabs.exe" }
    Start-Process $exe
    Start-Sleep -Seconds 6
    $proc = Get-Process FileTabs | Sort-Object StartTime -Descending | Select-Object -First 1
}
if (-not $proc) { Fail "instance" "no FileTabs process after launch attempt"; exit 1 }
Pass "instance" "PID $($proc.Id)"
Start-Sleep -Seconds 2   # hot-key is registered shortly after startup

# Close any pads left over from a previous run so the first hot-key press
# lands on a clean state (WM_CLOSE; safe for pads holding staged files too,
# the app treats close as dismiss without dropping anything).
$stale = [DropPointNative]::CloseAllPads([uint32]$proc.Id)
if ($stale -gt 0) { Write-Host "closed $stale stale pad(s) from a previous run"; Start-Sleep -Seconds 1 }

# ---------- 1. log baseline ----------
$log = Join-Path $env:APPDATA "FileTabs\app.log"
$baseLength = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

# ---------- 2. send Shift+CapsLock x4 ----------
$capsWasOn = (([DropPointNative]::GetKeyState(0x14) -band 1) -eq 1)
try {
    foreach ($i in 1..4) {
        # ModShift|ModNoRepeat hot-key: shift down, caps tap, shift up.
        [DropPointNative]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [DropPointNative]::keybd_event(0x14, 0, 0, [UIntPtr]::Zero)
        [DropPointNative]::keybd_event(0x14, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [DropPointNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
    }
}
finally {
    # Shift+CapsLock toggled the light; put it back for the user.
    $capsNowOn = (([DropPointNative]::GetKeyState(0x14) -band 1) -eq 1)
    if ($capsNowOn -ne $capsWasOn) {
        [DropPointNative]::keybd_event(0x14, 0, 0, [UIntPtr]::Zero)
        [DropPointNative]::keybd_event(0x14, 0, 2, [UIntPtr]::Zero)
    }
}

Start-Sleep -Seconds 1
$newLines = @()
if (Test-Path $log) {
    $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($baseLength, 'Begin')
        $reader = New-Object System.IO.StreamReader($stream)
        $newLines = $reader.ReadToEnd() -split "`n"
    } finally { $stream.Dispose() }
}

# ---------- 3. assertions ----------
$created = @()
$reused = 0
foreach ($line in $newLines) {
    if ($line -match 'DropPoint pad shown at ([\d.]+),([\d.]+).*slot=(\d+)') {
        $created += [pscustomobject]@{
            X = [double]$matches[1]; Y = [double]$matches[2]; Slot = [int]$matches[3] }
    }
    if ($line -match 'reused existing pad') { $reused++ }
}

if ($created.Count -eq 1 -and $created[0].Slot -eq 0) {
    Pass "hot-key creates pad at slot 0" "position $($created[0].X),$($created[0].Y)"
} else {
    Fail "hot-key creates pad at slot 0" "saw $($created.Count) creations (slots: $(($created | ForEach-Object Slot) -join ','))"
}

if ($reused -ge 3) {
    Pass "empty pad reused on presses 2-4" "$reused reuse lines (PRD #10: empty pads are never duplicated)"
} else {
    Fail "empty pad reused on presses 2-4" "only $reused reuse lines for 3 repeat presses"
}

# ---------- 4. pad screenshot ----------
$outDir = Join-Path $PSScriptRoot "..\verify-droppoint"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$pads = [DropPointNative]::FindPadWindows([uint32]$proc.Id)
$shotIndex = 0
foreach ($h in $pads) {
    $rect = New-Object DropPointNative+RECT
    [void][DropPointNative]::GetWindowRect($h, [ref]$rect)
    $w = $rect.R - $rect.L; $hh = $rect.B - $rect.T
    if ($w -le 0 -or $hh -le 0) { continue }
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [void][DropPointNative]::PrintWindow($h, $dc, 2)
    $g.ReleaseHdc($dc); $g.Dispose()
    $shotIndex++
    $path = Join-Path $outDir "pad-$shotIndex.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Pass "pad screenshot" "$path (${w}x${hh})"
}
if ($shotIndex -eq 0) {
    Fail "pad screenshot" "no pad window found for PID $($proc.Id)"
}

# ---------- summary ----------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($failures.Count) CHECK(S) FAILED" -ForegroundColor Red
    exit 1
}
