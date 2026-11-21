# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Diagnostic: dump the preview window's text after a dry run, then see what the export does.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$DumpPath
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

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
Write-Host "dry run clicked, waiting 25s..."
Start-Sleep -Seconds 25

$main = Get-Main $proc.Id
$texts = Get-ElementNames -Root $main -Limit 600
$texts | ForEach-Object { "$($_.Type)`t$($_.Name)" } | Set-Content $DumpPath -Encoding utf8
Write-Host "dumped $($texts.Count) elements to $DumpPath"

Write-Host "=== lines mentioning failures ==="
Get-Content $DumpPath | Where-Object { $_ -match 'winget|respond|fall back|not on this PC|ready|failed' } | Select-Object -First 20

Write-Host "=== clicking Export report ==="
$main = Get-Main $proc.Id
$export = Find-ByName -Root $main -Name "Export report"
Write-Host "export element found: $([bool]$export)"
if ($export) { Invoke-Element $export }
Start-Sleep -Seconds 3
Write-Host "=== windows now ==="
Get-ProcessWindows -ProcessId $proc.Id | ForEach-Object { "  window: '$($_.Name)'" }
$proc.Refresh()
Write-Host "main window title: '$((Get-Process -Id $proc.Id).MainWindowTitle)'"

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
