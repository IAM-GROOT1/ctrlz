# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Diagnosis: reproduce the "download cancelled" line and record exactly which control was
# invoked. It lists every button (name + automation id) in every window before and after the
# click, invokes ONLY "Update now", then watches the prompt for 30 s without touching anything.
# A Cancel button is reported if it appears, but is never clicked.

param(
    [Parameter(Mandatory = $true)][string]$PortableExe,
    [Parameter(Mandatory = $true)][string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-RedactedLogTail', 'Save-Shot', 'Get-TopWindows') | Out-Null

$root = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test'
$data = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test-data'
$logFolder = Join-Path $data 'logs'

Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $root, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $root, $data, $ShotDir | Out-Null
$exe = Join-Path $root 'CTRL+Z.exe'
Copy-Item $PortableExe $exe -Force
Write-Host "exe version: $((Get-Item $exe).VersionInfo.FileVersion)"

$env:CTRLZ_DATA_DIR = $data
$env:CTRLZ_UPDATE_REPO = 'IAM-GROOT1/ctrlz-update-test'
$token = (gh auth token 2>$null)
if ($token) { $env:GITHUB_TOKEN = $token; Write-Host "token: present (not printed)" } else { Write-Host "token: absent" }

function Show-Buttons([int]$ProcessId, [string]$Label) {
    Write-Host "  buttons [$Label]:"
    foreach ($window in (Get-TopWindows -ProcessId $ProcessId)) {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($window.Handle)
        $all = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) {
            if ($item.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
                Write-Host ("    win='{0}' name='{1}' id='{2}' enabled={3}" -f $window.Title, $item.Current.Name, $item.Current.AutomationId, $item.Current.IsEnabled)
            }
        }
    }
}

$proc = Start-Process -FilePath $exe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Write-Host 'no window'; exit 1 }

$button = Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec 120
if (-not $button) {
    Invoke-FailureDump -ProcessId $proc.Id -Label 'no prompt' -LogFolder $logFolder -ShotDir $ShotDir
    exit 1
}
Show-Buttons -ProcessId $proc.Id -Label 'before click'
Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'prompt-diag.png') | Out-Null

Invoke-Element $button
Write-Host "invoked: name='$($button.Current.Name)' id='$($button.Current.AutomationId)' class='$($button.Current.ClassName)'"

for ($i = 1; $i -le 15; $i++) {
    Start-Sleep -Seconds 2
    $tail = (Get-RedactedLogTail -LogFolder $logFolder -Lines 3) -join ' / '
    $titles = ((Get-TopWindows -ProcessId $proc.Id) | ForEach-Object { $_.Title }) -join ' | '
    Write-Host ("  t+{0,2}s windows=[{1}] log={2}" -f ($i * 2), $titles, $tail)
    if ($i -eq 3) {
        Show-Buttons -ProcessId $proc.Id -Label 'during download'
        Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'prompt-diag-during.png') | Out-Null
    }
}

Write-Host "--- last 25 log lines (redacted) ---"
foreach ($line in (Get-RedactedLogTail -LogFolder $logFolder -Lines 25)) { Write-Host "  $line" }
