# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Diagnostic: why did the preview not appear? Prints button state, windows over time,
# a screenshot and the app log tail.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$Marker,
    [string]$ShotPath
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

$env:CTRLZ_DATA_DIR = $DataDir
$env:FAKE_INSTALLER_MARKER = $Marker
$env:FAKE_INSTALLER_SLEEP_MS = "2500"
Remove-Item $Marker -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 3

$nav = Wait-ByName -Root $main -Name "Install" -TimeoutSec 20
Invoke-Element $nav
Start-Sleep -Seconds 3

$proc.Refresh()
$main = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

Write-Host "=== page text (first 25 non-empty) ==="
Get-ElementNames -Root $main -Limit 150 |
    Where-Object { $_.Type -eq 'ControlType.Text' -and $_.Name } |
    Select-Object -First 25 | ForEach-Object { "  '$($_.Name)'" }

$start = Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 10
Write-Host "start button found: $([bool]$start)"
if ($start) { Write-Host "start button enabled: $($start.Current.IsEnabled)" }

Write-Host "=== clicking ==="
try { Invoke-Element $start; Write-Host "invoked ok" } catch { Write-Host "invoke failed: $($_.Exception.Message)" }

for ($i = 1; $i -le 10; $i++) {
    Start-Sleep -Seconds 1
    $names = (Get-ProcessWindows -ProcessId $proc.Id | ForEach-Object { $_.Name }) -join ' | '
    Write-Host "  t+${i}s windows: $names"
}

if ($ShotPath) {
    $proc.Refresh()
    $w = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $ok = Save-WindowShot -Element $w -Path $ShotPath
    Write-Host "screenshot saved: $ok -> $ShotPath"
}

Write-Host "=== app log tail ==="
$logDir = Join-Path $DataDir "logs"
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -Filter *.log | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { Get-Content $log.FullName -Tail 15 | ForEach-Object { "  $_" } }
} else {
    Write-Host "  (no log folder)"
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
