# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Diagnosis only: run the 1.2.0 portable from a normal folder (not %TEMP%), click Update now,
# wait, then dump the evidence. Nothing is deleted, so the log survives for reading.

param(
    [Parameter(Mandatory = $true)][string]$PortableV120,
    [int]$PromptTimeoutSec = 120,
    [int]$WaitAfterClickSec = 90
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts') | Out-Null

$root = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test'
$data = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test-data'
Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $root, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $root, $data | Out-Null

$exe = Join-Path $root 'CTRL+Z.exe'
Copy-Item $PortableV120 $exe -Force
Write-Host "exe: $exe version=$((Get-Item $exe).VersionInfo.FileVersion)"

$env:CTRLZ_DATA_DIR = $data
Remove-Item Env:\CTRLZ_UPDATE_REPO -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $exe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Write-Host "no window"; exit 1 }

$button = Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec $PromptTimeoutSec
Write-Host "prompt found: $([bool]$button)"
if ($button) {
    Invoke-Element $button
    Write-Host "clicked Update now at $(Get-Date -Format HH:mm:ss)"
}

Start-Sleep -Seconds $WaitAfterClickSec

Write-Host "=== process still alive? ==="
$alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
Write-Host "  old pid $($proc.Id) alive: $([bool]$alive)"
Write-Host "=== CTRL+Z processes ==="
Get-Process -Name 'CTRL+Z*' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  pid=$($_.Id) name=$($_.ProcessName)" }
Write-Host "=== files beside the exe ==="
Get-ChildItem $root -Force | ForEach-Object { Write-Host "  $($_.Name)  $([math]::Round($_.Length/1MB,2))MB  $($_.LastWriteTime.ToString('HH:mm:ss'))" }
Write-Host "  exe version now: $((Get-Item $exe).VersionInfo.FileVersion)"
Write-Host "=== last 80 log lines (redacted) ==="
foreach ($line in (Get-RedactedLogTail -LogFolder (Join-Path $data 'logs') -Lines 80)) { Write-Host "  $line" }
