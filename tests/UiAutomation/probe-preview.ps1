# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Probe: launch the app against the synthetic profile and print the UIA names it exposes,
# so the real assertions can be written against actual labels instead of guesses.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$Marker
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

$env:CTRLZ_DATA_DIR = $DataDir
$env:FAKE_INSTALLER_MARKER = $Marker
$env:FAKE_INSTALLER_SLEEP_MS = "2500"
Remove-Item $Marker -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Write-Host "main window: '$($main.Current.Name)'"

$nav = Wait-ByName -Root $main -Name "Install" -TimeoutSec 20
if (-not $nav) { Write-Host "NAV 'Install' NOT FOUND"; exit 1 }
Invoke-Element $nav
Start-Sleep -Seconds 3

$main = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $proc.Id).MainWindowHandle)
Write-Host "=== Install page buttons ==="
Get-ElementNames -Root $main | Where-Object { $_.Type -eq 'ControlType.Button' -and $_.Name } |
    ForEach-Object { "  button: '$($_.Name)'" }

$start = Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 10
if (-not $start) { Write-Host "'Install everything' NOT FOUND"; exit 1 }
Invoke-Element $start
Start-Sleep -Seconds 2

Write-Host "=== windows after pressing install ==="
foreach ($window in (Get-ProcessWindows -ProcessId $proc.Id)) {
    Write-Host "  window: '$($window.Name)'"
}

$preview = Wait-WindowNamed -ProcessId $proc.Id -Name "Preview install" -TimeoutSec 15
if ($preview) {
    Write-Host "=== preview window contents ==="
    Get-ElementNames -Root $preview -Limit 120 |
        Where-Object { $_.Name } | Select-Object -First 40 |
        ForEach-Object { "  $($_.Type): '$($_.Name)'" }
} else {
    Write-Host "PREVIEW WINDOW NOT FOUND"
}

Write-Host "marker exists during preview: $(Test-Path $Marker)"

# Leave everything closed; this probe only inspects labels.
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
