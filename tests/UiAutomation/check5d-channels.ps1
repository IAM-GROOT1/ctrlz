# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Channels: Stable must offer only v0.0.2 and Pre-release must also offer v0.0.3-beta.1.
#
# Uses a Debug build stamped 0.0.1 with CTRLZ_UPDATE_REPO pointed at the throwaway test repo and an
# isolated CTRLZ_DATA_DIR. The channel is changed through Settings > Updates, never by editing
# settings.json. Prints every control it finds when it cannot find the channel switch.

param(
    [Parameter(Mandatory = $true)][string]$PortableExe,
    [Parameter(Mandatory = $true)][string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts', 'Get-TopWindows', 'Save-Shot') | Out-Null

$root = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test'
$data = Join-Path $env:LOCALAPPDATA 'ctrlz-portable-test-data'
$results = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host "[PASS] $Name :: $Detail" } else { Write-Host "[FAIL] $Name :: $Detail" }
}

function Reset-TestArea {
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    Remove-Item $root, $data -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $root, $data, $ShotDir | Out-Null
    Copy-Item $PortableExe (Join-Path $root 'CTRL+Z.exe') -Force
}

function Start-TestApp {
    $env:CTRLZ_DATA_DIR = $data
    $env:CTRLZ_UPDATE_REPO = 'IAM-GROOT1/ctrlz-update-test'
    $proc = Start-Process -FilePath (Join-Path $root 'CTRL+Z.exe') -PassThru
    Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60 | Out-Null
    Start-Sleep -Seconds 3
    return $proc
}

function Open-Settings([int]$ProcessId) {
    $nav = Wait-NameIn -ProcessId $ProcessId -Name 'Settings' -TimeoutSec 25
    if ($nav) { Invoke-Element $nav; Start-Sleep -Seconds 3 }
}

function Invoke-Channel([int]$ProcessId, [string]$Name) {
    foreach ($root in (Get-SearchRoots -ProcessId $ProcessId)) {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) {
            if ($item.Current.Name -eq $Name) {
                # The channel control is a styled button that may not expose InvokePattern; try
                # toggle and selection before giving up.
                foreach ($pattern in @(
                    [System.Windows.Automation.InvokePattern]::Pattern,
                    [System.Windows.Automation.TogglePattern]::Pattern,
                    [System.Windows.Automation.SelectionItemPattern]::Pattern)) {
                    try {
                        $item.GetCurrentPattern($pattern)
                        if ($pattern -eq [System.Windows.Automation.InvokePattern]::Pattern) { $item.GetCurrentPattern($pattern).Invoke() }
                        elseif ($pattern -eq [System.Windows.Automation.TogglePattern]::Pattern) { $item.GetCurrentPattern($pattern).Toggle() }
                        else { $item.GetCurrentPattern($pattern).Select() }
                        return $true
                    } catch { }
                }
            }
        }
    }
    return $false
}

function Show-Controls([int]$ProcessId) {
    Write-Host "  controls mentioning a channel or update:"
    foreach ($root in (Get-SearchRoots -ProcessId $ProcessId)) {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) {
            if ($item.Current.Name -match 'Stable|Pre-release|Channel|update') {
                Write-Host ("    {0} name='{1}' id='{2}'" -f $item.Current.ControlType.ProgrammaticName, $item.Current.Name, $item.Current.AutomationId)
            }
        }
    }
}

Reset-TestArea
$proc = Start-TestApp
Open-Settings $proc.Id
$texts = (Get-Texts -ProcessId $proc.Id) -join ' || '
$stableOffers = $texts -match '0\.0\.2'
$stableHidesBeta = -not ($texts -match '0\.0\.3-beta')
Record "Stable offers 0.0.2" $stableOffers "mentioned=$stableOffers"
Record "Stable hides 0.0.3-beta.1" $stableHidesBeta "beta mentioned=$(-not $stableHidesBeta)"
if (-not $stableOffers) { Show-Controls $proc.Id; Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'channels-stable.png') | Out-Null }

$switched = Invoke-Channel $proc.Id 'Pre-release'
if (-not $switched) { $switched = Invoke-Channel $proc.Id 'Pre-release channel' }
Record "Pre-release switch found in Settings" $switched "invoked=$switched"
if ($switched) {
    $checkNow = Wait-NameIn -ProcessId $proc.Id -Name 'Check for updates' -TimeoutSec 10
    if ($checkNow) { try { Invoke-Element $checkNow } catch { } }
    Start-Sleep -Seconds 8
    $preTexts = (Get-Texts -ProcessId $proc.Id) -join ' || '
    $preOffers = $preTexts -match '0\.0\.3-beta'
    Record "Pre-release offers 0.0.3-beta.1" $preOffers "mentioned=$preOffers"
    if (-not $preOffers) { Show-Controls $proc.Id; Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'channels-prerelease.png') | Out-Null }
}

Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\CTRLZ_DATA_DIR, Env:\CTRLZ_UPDATE_REPO -ErrorAction SilentlyContinue
$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 5D: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
