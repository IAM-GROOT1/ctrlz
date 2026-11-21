# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Probe: print only the readiness summary texts (counts, no names) and time the Cancel button.

param([string]$AppExe)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

Remove-Item Env:\CTRLZ_DATA_DIR -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2
Invoke-Element (Wait-ByName -Root $main -Name "Ready to wipe" -TimeoutSec 20)

for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Milliseconds 700
    $main = Get-Main $proc.Id
    $texts = @(Get-ElementNames -Root $main -Limit 1200 | Where-Object { $_.Name } | ForEach-Object { $_.Name })
    $summary = $texts | Where-Object { $_ -match 'pass|warning|fail|attention|Ready to wipe|Scanning|Cancel' } | Select-Object -First 6
    $hasCancel = [bool](Find-ByName -Root $main -Name "Cancel")
    Write-Host ("t+{0:N1}s cancel={1} | {2}" -f (($i+1)*0.7), $hasCancel, ($summary -join ' // '))
}
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
