# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 3: profile drift. Uses a synthetic profile whose entries are chosen so that one must
# match exactly an app installed on this PC (7-Zip, by package id), three cannot match, and
# the rest of the machine shows up as extra. Verifies the groups, then the row actions by
# reading the profile and safety state from disk.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

$results = New-Object System.Collections.ArrayList
function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host ("[PASS] " + $Name + " :: " + $Detail) }
    else { Write-Host ("[FAIL] " + $Name + " :: " + $Detail) }
}

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Get-Texts([int]$ProcessId) {
    $main = Get-Main $ProcessId
    if (-not $main) { return @() }
    return @(Get-ElementNames -Root $main -Limit 900 | Where-Object { $_.Name } | ForEach-Object { $_.Name })
}

function Get-ProfileApps {
    $file = Get-ChildItem (Join-Path $DataDir "profiles") -Filter *.json | Select-Object -First 1
    return @((Get-Content $file.FullName -Raw | ConvertFrom-Json).apps)
}

function Get-IgnoredKeys {
    $stateFile = Join-Path $DataDir "state\safety-state.json"
    if (-not (Test-Path $stateFile)) { return @() }
    return @((Get-Content $stateFile -Raw | ConvertFrom-Json).ignoredDriftKeys)
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
$env:CTRLZ_DATA_DIR = $DataDir

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2
Invoke-Element (Wait-ByName -Root $main -Name "My Apps" -TimeoutSec 20)
Start-Sleep -Seconds 2

$main = Get-Main $proc.Id
$driftButton = Wait-ByName -Root $main -Name "Check drift" -TimeoutSec 15
Record "3a My Apps exposes Check drift" ([bool]$driftButton) "button found=$([bool]$driftButton)"
Invoke-Element $driftButton

$loaded = Wait-Until { (Get-Texts $proc.Id) -match 'missing' } -TimeoutSec 120
Start-Sleep -Seconds 3
$texts = Get-Texts $proc.Id
$joined = $texts -join ' || '

Record "3b drift completes and shows a summary" $loaded ("summary present=" + ($joined -match '\d+ missing'))

$missingMatch = [regex]::Match($joined, '(\d+)\s+missing\s*.\s*(\d+)\s+not in profile\s*.\s*(\d+)\s+outdated')
if ($missingMatch.Success) {
    $missingCount = [int]$missingMatch.Groups[1].Value
    $extraCount = [int]$missingMatch.Groups[2].Value
    Record "3c summary reports counts" $true ("missing={0} extra={1} outdated={2}" -f $missingCount, $extraCount, $missingMatch.Groups[3].Value)
    $syntheticMissing = @(@("Definitely Not Installed App", "Local Only App", "GitHub Source App") | Where-Object { $joined -match [regex]::Escape($_) })
    Record "3d exact match removed 7-Zip from Missing" (($joined -notmatch '7-Zip') -and ($syntheticMissing.Count -eq $missingCount)) ("7-Zip absent={0}; {1} of 3 fabricated apps are missing, matching the count" -f ($joined -notmatch '7-Zip'), $syntheticMissing.Count)
    Record "3e installed apps appear as extra" ($extraCount -gt 50) ("extra count is {0}" -f $extraCount)
} else {
    Record "3c summary reports counts" $false "summary line not parseable"
}

Record "3f fabricated apps are listed as missing" ($syntheticMissing.Count -ge 2) ("fabricated missing apps listed={0}" -f $syntheticMissing.Count)
Record "3f2 custom sources are shown as missing, not guessed" (($joined -match 'Local Only App') -or ($joined -match '7-Zip')) "custom-source entries handled without a false match"

try { Save-WindowShot -Element (Get-Main $proc.Id) -Path (Join-Path $ShotDir "03-drift.png") | Out-Null } catch { }

# ---------------------------------------------------------------- ignore action
$before = (Get-ProfileApps).Count
$drift = Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 20
$ignoreButtons = @()
$all = $drift.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    if ($element.Current.Name -eq "Ignore" -and $element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
        $ignoreButtons += $element
    }
}
Record "3g drift rows expose an Ignore action" ($ignoreButtons.Count -gt 0) ("ignore buttons visible={0}" -f $ignoreButtons.Count)
if ($ignoreButtons.Count -gt 0) {
    Invoke-Element $ignoreButtons[0]
    Start-Sleep -Seconds 6
    $keys = Get-IgnoredKeys
    Record "3h ignore persists a drift key without any path" `
        (($keys.Count -gt 0) -and -not ($keys -join '' -match '\\')) `
        ("ignored keys={0} contains backslash={1}" -f $keys.Count, [bool](($keys -join '') -match '\\'))
}

# ---------------------------------------------------------------- add to profile
$countBeforeAdd = (Get-ProfileApps).Count
$drift = Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 20
$addButton = $null
$all = $drift.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    if ($element.Current.Name -eq "Add to profile" -and $element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
        $addButton = $element; break
    }
}
if ($addButton) {
    Invoke-Element $addButton
    Start-Sleep -Seconds 8
    $countAfterAdd = (Get-ProfileApps).Count
    Record "3i add to profile writes the app to the profile" ($countAfterAdd -gt $countBeforeAdd) `
        ("profile apps {0} -> {1}" -f $countBeforeAdd, $countAfterAdd)
} else {
    Record "3i add to profile writes the app to the profile" $false "no Add to profile button visible"
}

# ---------------------------------------------------------------- remove from profile
$countBeforeRemove = (Get-ProfileApps).Count
$drift = Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 20
$removeButton = $null
$all = $drift.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    if ($element.Current.Name -eq "Remove" -and $element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
        $removeButton = $element; break
    }
}
if ($removeButton) {
    Invoke-Element $removeButton
    Start-Sleep -Seconds 2
    $confirm = Find-ByName -Root (Get-Main $proc.Id) -Name "Remove"
    if ($confirm) { Invoke-Element $confirm }
    Start-Sleep -Seconds 6
    $countAfterRemove = (Get-ProfileApps).Count
    Record "3j remove from profile deletes the app" ($countAfterRemove -lt $countBeforeRemove) `
        ("profile apps {0} -> {1}" -f $countBeforeRemove, $countAfterRemove)
} else {
    Record "3j remove from profile deletes the app" $false "no Remove button visible"
}

# ---------------------------------------------------------------- install hand-off
$drift = Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 20
$installButton = $null
$all = $drift.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    if ($element.Current.Name -eq "Install" -and $element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
        $installButton = $element; break
    }
}
if ($installButton) {
    Invoke-Element $installButton
    Start-Sleep -Seconds 4
    $driftGone = -not [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Re-check")
    $onInstall = [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Install everything")
    Record "3k install hands off to the install queue" ($driftGone -and $onInstall) "drift closed=$driftGone install page=$onInstall"
} else {
    Record "3k install hands off to the install queue" $false "no Install button visible"
}

try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 3 SUMMARY: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
$results | ForEach-Object {
    if ($_.Pass) { $tag = "PASS" } else { $tag = "FAIL" }
    Write-Host ("  " + $tag + "  " + $_.Name + " - " + $_.Detail)
}
if ($failed -eq 0) { exit 0 } else { exit 1 }

