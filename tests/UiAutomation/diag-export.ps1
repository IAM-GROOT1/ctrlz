# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Diagnostic: after a dry run, click Export and inspect windows at the Win32 level, then try
# typing the target path into the save dialog.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$ExportPath
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Win {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static List<string> ListWindows(uint target) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
                var c = new StringBuilder(256); GetClassNameW(h, c, 256);
                list.Add(c.ToString() + "|" + t.ToString() + "|" + h.ToInt64());
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

Remove-Item (Join-Path $DataDir "state\install-state.json") -Force -ErrorAction SilentlyContinue
$env:CTRLZ_DATA_DIR = $DataDir

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2
Invoke-Element (Wait-ByName -Root $main -Name "Install" -TimeoutSec 20)
Start-Sleep -Seconds 2
$main = Get-Main $proc.Id
Invoke-Element (Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 10)
Wait-ByName -Root (Get-Main $proc.Id) -Name "Dry run" -TimeoutSec 25 | Out-Null
$main = Get-Main $proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Dry run")
Start-Sleep -Seconds 30

$main = Get-Main $proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Export report")
Start-Sleep -Seconds 3

Write-Host "=== Win32 visible windows of process ==="
$windows = [Win]::ListWindows([uint32]$proc.Id)
foreach ($w in $windows) { Write-Host "  $w" }

$saveDialog = $windows | Where-Object { $_ -match 'Save|save' } | Select-Object -First 1
if (-not $saveDialog) { Write-Host "NO SAVE DIALOG WINDOW"; Stop-Process -Id $proc.Id -Force; exit 1 }

$hwnd = [IntPtr][int64](($saveDialog -split '\|')[2])
[Win]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500
Write-Host "=== typing path via SendKeys ==="
[System.Windows.Forms.SendKeys]::SendWait($ExportPath)
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Seconds 3
Write-Host "export exists: $(Test-Path $ExportPath)"

Write-Host "=== windows after save ==="
[Win]::ListWindows([uint32]$proc.Id) | ForEach-Object { Write-Host "  $_" }

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
