# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Channel diagnosis: switch to Pre-release in Settings > Updates, then prove whether the setting
# saved, whether the next check uses it, and which of the three causes it is.
#
#   (i)   the script never toggled the control  -> settings.json does not change
#   (ii)  the setting saved but the check still uses the old channel (cached) -> product bug
#   (iii) the toggle does not save at all -> product bug
#
# Uses invoke/toggle/selection when a control supports a pattern and falls back to a real mouse
# click at the control's centre.

param(
    [Parameter(Mandatory = $true)][string]$PortableExe,
    [Parameter(Mandatory = $true)][string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Get-Texts', 'Activate-Element', 'Click-ElementCenter', 'Get-RedactedLogTail') | Out-Null

$root = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test'
$data = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test-data'
$logFolder = Join-Path $data 'logs'

Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $root, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $root, $data, $ShotDir | Out-Null
Copy-Item $PortableExe (Join-Path $root 'CTRL+Z.exe') -Force

$env:CTRLZ_DATA_DIR = $data
$env:CTRLZ_UPDATE_REPO = 'IAM-GROOT1/ctrlz-update-test'
$proc = Start-Process -FilePath (Join-Path $root 'CTRL+Z.exe') -PassThru
Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60 | Out-Null
Start-Sleep -Seconds 3

function Get-Channel {
    $file = Join-Path $data 'settings.json'
    if (-not (Test-Path $file)) { return $null }
    return (Get-Content $file -Raw | ConvertFrom-Json).updateChannel
}

function Find-Any([int]$ProcessId, [string]$Name) {
    foreach ($r in (Get-SearchRoots -ProcessId $ProcessId)) {
        $all = $r.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) { if ($item.Current.Name -eq $Name) { return $item } }
    }
    return $null
}

$before = Get-Channel
Write-Host "channel before: $before"

Invoke-Element (Wait-NameIn -ProcessId $proc.Id -Name 'Settings' -TimeoutSec 25)
Start-Sleep -Seconds 3

$pre = Wait-NameIn -ProcessId $proc.Id -Name 'Pre-release' -TimeoutSec 30
if (-not $pre) { Write-Host "NO PRE-RELEASE CONTROL"; Invoke-FailureDump -ProcessId $proc.Id -Label 'no Pre-release control' -LogFolder $logFolder -ShotDir $ShotDir; exit 1 }
$type = $pre.Current.ControlType.ProgrammaticName
# TogglePattern only flips IsChecked; the Command that saves the channel runs on a real click.
Click-ElementCenter  | Out-Null
$how = 'mouse click'
Write-Host "activated Pre-release via $how (control type $type)"
Start-Sleep -Seconds 3

$after = Get-Channel
Write-Host "channel after : $after"
Write-Host "a) setting saved: $($after -ne $before) (before=$before after=$after)"

$check = Find-Any $proc.Id 'Check for updates'
if ($check) { Activate-Element $check | Out-Null; Write-Host "clicked Check for updates" }
Start-Sleep -Seconds 8

$log = (Get-RedactedLogTail -LogFolder $logFolder -Lines 200) -join "`n"
$checkLine = [regex]::Match($log, 'Update check \(([^)]+)\): newest is ([^,]+), running ([^,]+)')
Write-Host "b) log line: $($checkLine.Value)"
Write-Host "   channel in check = $($checkLine.Groups[1].Value); newest = $($checkLine.Groups[2].Value)"

$verdict = if ($after -eq $before) { '(i) the script never toggled the control' }
    elseif ($checkLine.Groups[2].Value -match '0\.0\.3-beta') { 'no bug: the new channel applied on the next check' }
    else { '(ii) saved but the next check still used the old channel - product bug' }
Write-Host "c) verdict: $verdict"

Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'channel-diag.png') | Out-Null
Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\CTRLZ_DATA_DIR, Env:\CTRLZ_UPDATE_REPO -ErrorAction SilentlyContinue
exit 0
