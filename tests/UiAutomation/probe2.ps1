# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Probe 2: with the install state cleared, press "Install everything" and look for the preview
# both as a top-level window and inside the main window's UIA subtree.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$Marker
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

# Clear any saved install session so the button reads "Install everything".
$stateFile = Join-Path $DataDir "state\install-state.json"
Remove-Item $stateFile -Force -ErrorAction SilentlyContinue
Remove-Item $Marker -Force -ErrorAction SilentlyContinue

$env:CTRLZ_DATA_DIR = $DataDir
$env:FAKE_INSTALLER_MARKER = $Marker
$env:FAKE_INSTALLER_SLEEP_MS = "2500"

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 3
Invoke-Element (Wait-ByName -Root $main -Name "Install" -TimeoutSec 20)
Start-Sleep -Seconds 3

$proc.Refresh()
$main = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

Write-Host "=== install page buttons ==="
Get-ElementNames -Root $main -Limit 200 | Where-Object { $_.Type -eq 'ControlType.Button' -and $_.Name } |
    ForEach-Object { "  '$($_.Name)'" }

$start = Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 10
Write-Host "start found: $([bool]$start) enabled: $($start.Current.IsEnabled)"
if (-not $start) { Stop-Process -Id $proc.Id -Force; exit 1 }

Invoke-Element $start
Write-Host "clicked at $(Get-Date -Format o)"

for ($i = 1; $i -le 6; $i++) {
    Start-Sleep -Seconds 1
    $proc.Refresh()
    $main = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $installNow = Find-ByName -Root $main -Name "Install now"
    $cancel = Find-ByName -Root $main -Name "Cancel"
    $admin = Find-ByName -Root $main -Name "Administrator access"
    $resume = Find-ByName -Root $main -Name "Resume install"
    $windows = (Get-ProcessWindows -ProcessId $proc.Id | ForEach-Object { $_.Name }) -join ' | '
    Write-Host "  t+${i}s  installNow=$([bool]$installNow) cancel=$([bool]$cancel) adminDialog=$([bool]$admin) resume=$([bool]$resume)"
    Write-Host "        windows: $windows"
}

Write-Host "marker during preview: $(Test-Path $Marker)"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
