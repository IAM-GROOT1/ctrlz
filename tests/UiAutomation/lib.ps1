# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Shared UI Automation helpers for the updater checks. Dot-source once:
#     . "$PSScriptRoot\lib.ps1"
#     Assert-Helpers -Names @('Wait-AppWindow','Wait-NameIn')
#
# Every wait has a timeout, reports progress every 10 seconds, and on failure calls
# Invoke-FailureDump (open windows, redacted log tail, screenshot) instead of looping silently.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (-not ('CtrlZWin32' -as [type])) {
    Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class CtrlZWin32 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static List<string> Windows(uint target) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
                list.Add(pid.ToString() + "|" + h.ToInt64().ToString() + "|" + t.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@
}

$script:UiRequired = @(
    'Assert-Helpers', 'Get-TopWindows', 'Wait-AppWindow', 'Find-ByName', 'Wait-NameIn',
    'Invoke-Element', 'Get-Texts', 'Save-Shot', 'Invoke-FailureDump', 'Get-RedactedLogTail'
)

function Assert-Helpers {
    param([string[]]$Names)
    $missing = @($Names | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) })
    if ($missing.Count -gt 0) { throw "Missing helper(s): $($missing -join ', ')" }
    Write-Host "[lib] helpers present: $($Names -join ', ')"
}

function Get-TopWindows {
    param([int]$ProcessId)
    $out = @()
    foreach ($entry in [CtrlZWin32]::Windows([uint32]$ProcessId)) {
        $parts = $entry -split '\|', 3
        $out += [pscustomobject]@{ ProcessId = [int]$parts[0]; Handle = [IntPtr][int64]$parts[1]; Title = $parts[2] }
    }
    return $out
}

function Wait-AppWindow {
    param([int]$ProcessId, [int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $next = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc) {
            $proc.Refresh()
            if ($proc.MainWindowHandle -ne 0) {
                return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            }
        }
        if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the main window"; $next = (Get-Date).AddSeconds(10) }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-ByName {
    param($Root, [string]$Name)
    if (-not $Root) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-SearchRoots {
    param([int]$ProcessId)
    $roots = @()
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($proc) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) {
            $roots += [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
        }
    }
    foreach ($window in Get-TopWindows -ProcessId $ProcessId) {
        $roots += [System.Windows.Automation.AutomationElement]::FromHandle($window.Handle)
    }
    return $roots
}

function Wait-NameIn {
    param([int]$ProcessId, [string]$Name, [int]$TimeoutSec = 30, [switch]$Quiet)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $next = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        foreach ($root in (Get-SearchRoots -ProcessId $ProcessId)) {
            $found = Find-ByName -Root $root -Name $Name
            if ($found) { return $found }
        }
        if ((Get-Date) -gt $next) {
            if (-not $Quiet) { Write-Host "  ... still waiting for '$Name'" }
            $next = (Get-Date).AddSeconds(10)
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-Element {
    param($Element)
    if (-not $Element) { throw "Cannot invoke a null element" }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-Texts {
    param([int]$ProcessId, [int]$Limit = 900)
    $names = @()
    foreach ($root in (Get-SearchRoots -ProcessId $ProcessId)) {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $all) {
            if ($element.Current.Name) { $names += $element.Current.Name }
            if ($names.Count -ge $Limit) { return $names }
        }
    }
    return $names
}

function Save-Shot {
    param([int]$ProcessId, [string]$Path)
    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $proc) { return $false }
        $proc.Refresh()
        if ($proc.MainWindowHandle -eq 0) { return $false }
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) { return $false }
        $bitmap = New-Object System.Drawing.Bitmap ([int]$rect.Width), ([int]$rect.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
        New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose(); $bitmap.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Get-RedactedLogTail {
    param([string]$LogFolder, [int]$Lines = 50)
    if (-not (Test-Path $LogFolder)) { return @('(no log folder)') }
    $file = Get-ChildItem $LogFolder -Filter *.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return @('(no log file)') }
    $tail = @(Get-Content $file.FullName -Tail $Lines -ErrorAction SilentlyContinue)
    # Counts and messages only: strip any drive-qualified or UNC path before printing.
    return @($tail | ForEach-Object { [regex]::Replace($_, '[A-Za-z]:\\[^ "'']*|\\\\[^\\\s]+\S*', '<path>') })
}

function Invoke-FailureDump {
    param([int]$ProcessId, [string]$Label, [string]$LogFolder, [string]$ShotDir)
    Write-Host "[FAILURE] $Label"
    Write-Host "  open windows:"
    foreach ($window in (Get-TopWindows -ProcessId $ProcessId)) { Write-Host "    pid=$($window.ProcessId) '$($window.Title)'" }
    Write-Host "  log tail (redacted):"
    foreach ($line in (Get-RedactedLogTail -LogFolder $LogFolder)) { Write-Host "    $line" }
    if ($ShotDir) {
        $shot = Join-Path $ShotDir ("failure-" + (Get-Date -Format "HHmmss") + ".png")
        $ok = Save-Shot -ProcessId $ProcessId -Path $shot
        Write-Host "  screenshot: $shot (saved=$ok)"
    }
}

if (-not ('CtrlZMouse' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CtrlZMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@
}

function Click-ElementCenter {
    param($Element)
    if (-not $Element) { throw "Cannot click a null element" }
    $rect = $Element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw "Element has no clickable bounds" }
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    [CtrlZMouse]::Click($x, $y)
    return $true
}

function Activate-Element {
    param($Element)
    foreach ($pattern in @(
        [System.Windows.Automation.InvokePattern]::Pattern,
        [System.Windows.Automation.TogglePattern]::Pattern,
        [System.Windows.Automation.SelectionItemPattern]::Pattern)) {
        try {
            $p = $Element.GetCurrentPattern($pattern)
            if ($pattern -eq [System.Windows.Automation.InvokePattern]::Pattern) { $p.Invoke(); return 'invoke' }
            if ($pattern -eq [System.Windows.Automation.TogglePattern]::Pattern) { $p.Toggle(); return 'toggle' }
            $p.Select(); return 'select'
        } catch { }
    }
    Click-ElementCenter $Element | Out-Null
    return 'mouse'
}