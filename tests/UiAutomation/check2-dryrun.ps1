# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 2: dry run reports each broken source correctly (bogus winget id, dead URL, missing
# local file, GitHub asset that no longer exists) and the exported report carries no personal
# paths. Synthetic profile only; nothing is installed.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$ShotDir,
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

$results = New-Object System.Collections.ArrayList
function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host ("[PASS] " + $Name + " :: " + $Detail) }
    else { Write-Host ("[FAIL] " + $Name + " :: " + $Detail) }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Get-PageText([int]$ProcessId) {
    $main = Get-Main $ProcessId
    if (-not $main) { return "" }
    $texts = Get-ElementNames -Root $main -Limit 400
    return ($texts | Where-Object { $_.Name } | ForEach-Object { $_.Name }) -join " || "
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
Remove-Item (Join-Path $DataDir "state\install-state.json") -Force -ErrorAction SilentlyContinue
Remove-Item $ExportPath -Force -ErrorAction SilentlyContinue
$env:CTRLZ_DATA_DIR = $DataDir
$env:FAKE_INSTALLER_MARKER = (Join-Path $DataDir "should-not-exist.log")
Remove-Item $env:FAKE_INSTALLER_MARKER -Force -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2
Invoke-Element (Wait-ByName -Root $main -Name "Install" -TimeoutSec 20)
Start-Sleep -Seconds 2

$main = Get-Main $proc.Id
Invoke-Element (Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 10)
$preview = Wait-Until { [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Dry run") } -TimeoutSec 25
Record "2a preview opens for the dry run" $preview "preview shown=$preview"

$main = Get-Main $proc.Id
$before = Get-PageText $proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Dry run")

$finished = Wait-Until { (Get-PageText $proc.Id) -match 'ready\s+.*warnings' } -TimeoutSec 90
$text = Get-PageText $proc.Id
Record "2b dry run completes" $finished ("page mentions a ready/warning/failed summary=" + ($text -match 'ready'))

# GitHub's API is occasionally unreachable from this machine; retry the dry run so a transient
# network failure is not mistaken for a classification bug.
$githubRetries = 0
while ($githubRetries -lt 3) {
    $pageCheck = Get-PageText $proc.Id
    if ($pageCheck -notmatch 'Could not reach GitHub') { break }
    $githubRetries++
    Write-Host "GitHub unreachable, retrying dry run ($githubRetries/3)..."
    Start-Sleep -Seconds 5
    $retryButton = Find-ByName -Root (Get-Main $proc.Id) -Name "Dry run"
    if ($retryButton) { Invoke-Element $retryButton; Start-Sleep -Seconds 30 }
}

try { Save-WindowShot -Element (Get-Main $proc.Id) -Path (Join-Path $ShotDir "02-dryrun.png") | Out-Null } catch { }

# ---------------------------------------------------------------- export the report
# The save dialog is a Win32 common dialog (#32770); drive it with SendKeys.
$main = Get-Main $proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Export report")

$dialogHandle = $null
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 300
    $candidate = [Win]::ListWindows([uint32]$proc.Id) | Where-Object { $_ -like '#32770|*' } | Select-Object -First 1
    if ($candidate) { $dialogHandle = [IntPtr][int64](($candidate -split '\|')[2]); break }
}
Record "2d export opens a save dialog" ($null -ne $dialogHandle) "dialog found=$($null -ne $dialogHandle)"

if ($dialogHandle) {
    [Win]::SetForegroundWindow($dialogHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait($ExportPath)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
}

$written = Wait-Until { Test-Path $ExportPath } -TimeoutSec 25
Record "2e report file is written" $written "exists=$(Test-Path $ExportPath)"

$report = ""
if (Test-Path $ExportPath) {
    $report = Get-Content $ExportPath -Raw

    Record "2c dry run classifies every source" `
        (($report -match 'Summary: 1 ready, 1 warnings, 3 failed')) `
        "report summary is 1 ready / 1 warning / 3 failed (github retries=$githubRetries)"
    Record "2f bogus winget id reported as a failure" ($report -match "winget cannot find") "contains 'winget cannot find'"
    Record "2g dead URL reported as a failure" ($report -match '\[FAIL\] Dead URL App') "Dead URL App marked FAIL"
    Record "2h missing local file reported as a failure" ($report -match 'not on this PC') "contains 'not on this PC'"
    Record "2i GitHub asset substitution warns with both names" `
        (($report -match '\[WARN\] GitHub Fallback App') -and ($report -match 'does-not-exist\.exe') -and ($report -match 'will fall back to')) `
        "WARN line names the recorded asset and the substitute"
    Record "2j a good winget id is ready" (($report -match '\[OK  \] Good Winget App') -and ($report -match 'winget resolves 7zip\.7zip')) "7zip resolves"

    $hasPersonal = ($report -match [regex]::Escape($env:USERPROFILE)) -or ($report -match 'C:\\Users\\')
    Record "2k exported report contains no personal paths" (-not $hasPersonal) "personal path present=$hasPersonal"
    Record "2l exported report says nothing was installed" ($report -match 'Nothing was installed') "disclaimer present"
}

$marker = Join-Path $DataDir "should-not-exist.log"
Record "2m dry run installed nothing" (-not (Test-Path $marker)) "installer marker absent=$(-not (Test-Path $marker))"

try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
Get-Process -Name "FakeInstaller" -ErrorAction SilentlyContinue | Stop-Process -Force

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 2 SUMMARY: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
$results | ForEach-Object {
    if ($_.Pass) { $tag = "PASS" } else { $tag = "FAIL" }
    Write-Host ("  " + $tag + "  " + $_.Name + " - " + $_.Detail)
}
if ($failed -eq 0) { exit 0 } else { exit 1 }
