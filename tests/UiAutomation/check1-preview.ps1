# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 1: the install preview appears before anything runs, blocks the run until confirmed,
# and cancelling it runs nothing. Also verifies the Settings toggle turns it off and back on.
#
# Each scenario starts a fresh app process with the on-disk install state cleared, so no
# in-memory session can interfere. Synthetic profile + FakeInstaller only.

param(
    [string]$AppExe,
    [string]$DataDir,
    [string]$Marker,
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

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Get-MarkerLines {
    if (-not (Test-Path $Marker)) { return @() }
    return @(Get-Content $Marker -ErrorAction SilentlyContinue)
}

function Reset-InstallState {
    Remove-Item (Join-Path $DataDir "state\install-state.json") -Force -ErrorAction SilentlyContinue
}

function Start-Session {
    Reset-InstallState
    $script:proc = Start-Process -FilePath $AppExe -PassThru
    $main = Wait-AppWindow -ProcessId $script:proc.Id -TimeoutSec 60
    Start-Sleep -Seconds 2
    Invoke-Element (Wait-ByName -Root $main -Name "Install" -TimeoutSec 20)
    Start-Sleep -Seconds 2
    return (Get-Main $script:proc.Id)
}

function Stop-Session {
    if ($script:proc) {
        try { Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Get-Process -Name "FakeInstaller" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

function Click-Start([int]$ProcessId) {
    $main = Get-Main $ProcessId
    $start = Wait-ByName -Root $main -Name "Install everything" -TimeoutSec 8
    if ($start) { Invoke-Element $start; return "Install everything" }
    return $null
}

function Wait-ForPreview([int]$ProcessId, [int]$TimeoutSec = 20) {
    return Wait-Until { [bool](Find-ByName -Root (Get-Main $ProcessId) -Name "Install now") } -TimeoutSec $TimeoutSec
}

function Wait-ForAdmin([int]$ProcessId, [int]$TimeoutSec = 20) {
    return Wait-Until { [bool](Find-ByName -Root (Get-Main $ProcessId) -Name "Continue without admin") } -TimeoutSec $TimeoutSec
}

function Wait-ForEnds([int]$Count, [int]$TimeoutSec = 120) {
    return Wait-Until { @(Get-MarkerLines | Where-Object { $_ -like 'END|*' }).Count -ge $Count } -TimeoutSec $TimeoutSec
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
$env:CTRLZ_DATA_DIR = $DataDir
$env:FAKE_INSTALLER_MARKER = $Marker
$env:FAKE_INSTALLER_SLEEP_MS = "2500"

# ================================================================ A: preview blocks, cancel runs nothing
Write-Host "--- scenario A: preview blocks the run"
Remove-Item $Marker -Force -ErrorAction SilentlyContinue
$main = Start-Session

$T_before = Get-Date
Click-Start $script:proc.Id | Out-Null
$shown = Wait-ForPreview $script:proc.Id
$T_preview = Get-Date
Record "1a preview appears" $shown ("{0:N1}s after pressing Install" -f ($T_preview - $T_before).TotalSeconds)

$main = Get-Main $script:proc.Id
$adminEarly = [bool](Find-ByName -Root $main -Name "Continue without admin")
Record "1b no elevation prompt before the preview" (-not $adminEarly) "admin dialog present=$adminEarly"

Start-Sleep -Seconds 3
$linesDuring = (Get-MarkerLines).Count
$procsDuring = @(Get-Process -Name "FakeInstaller" -ErrorAction SilentlyContinue).Count
Record "1c nothing runs while the preview is open" (($linesDuring -eq 0) -and ($procsDuring -eq 0)) `
    ("marker lines={0} installer processes={1} after 3s" -f $linesDuring, $procsDuring)

try { Save-WindowShot -Element $main -Path (Join-Path $ShotDir "01-preview.png") | Out-Null } catch { }

$main = Get-Main $script:proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Cancel")
$closed = Wait-Until { -not (Find-ByName -Root (Get-Main $script:proc.Id) -Name "Install now") } -TimeoutSec 15
Start-Sleep -Seconds 2
$linesAfterCancel = (Get-MarkerLines).Count
$adminAfterCancel = [bool](Find-ByName -Root (Get-Main $script:proc.Id) -Name "Continue without admin")
Record "1d cancel closes the preview" $closed "preview gone=$closed"
Record "1e cancel runs nothing" (($linesAfterCancel -eq 0) -and (-not $adminAfterCancel)) `
    ("marker lines={0} admin dialog={1}" -f $linesAfterCancel, $adminAfterCancel)
Stop-Session

# ================================================================ B: confirming runs the queue
Write-Host "--- scenario B: confirming runs all three apps"
Remove-Item $Marker -Force -ErrorAction SilentlyContinue
$main = Start-Session
Click-Start $script:proc.Id | Out-Null
$shown2 = Wait-ForPreview $script:proc.Id
Record "1f preview appears for the confirmed run" $shown2 "preview shown=$shown2"

$main = Get-Main $script:proc.Id
$T_confirm = Get-Date
Invoke-Element (Find-ByName -Root $main -Name "Install now")

$adminShown = Wait-ForAdmin $script:proc.Id
$linesAtAdmin = (Get-MarkerLines).Count
Record "1g elevation prompt follows the preview" $adminShown "admin dialog appeared=$adminShown"
Record "1h nothing ran before the elevation prompt" ($linesAtAdmin -eq 0) "marker lines=$linesAtAdmin"

$main = Get-Main $script:proc.Id
Invoke-Element (Find-ByName -Root $main -Name "Continue without admin")

$ranAll = Wait-ForEnds 3
$lines = Get-MarkerLines
$starts = @($lines | Where-Object { $_ -like 'START|*' })
$ends = @($lines | Where-Object { $_ -like 'END|*' })
Record "1i confirming runs all three apps" ($ranAll -and $starts.Count -eq 3 -and $ends.Count -eq 3) `
    ("starts={0} ends={1}" -f $starts.Count, $ends.Count)

if ($starts.Count -gt 0) {
    $firstStart = [DateTimeOffset]::Parse(($starts[0] -split '\|')[2])
    $delta = ($firstStart - [DateTimeOffset]$T_confirm).TotalSeconds
    Record "1j the first installer started only after confirmation" ($delta -ge 0) `
        ("first START was {0:N1}s after Install now" -f $delta)
}
Stop-Session

# ================================================================ C: toggle off skips the preview
Write-Host "--- scenario C: preview toggle off"
Remove-Item $Marker -Force -ErrorAction SilentlyContinue
$main = Start-Session
Invoke-Element (Wait-ByName -Root $main -Name "Settings" -TimeoutSec 15)
Start-Sleep -Seconds 2
$main = Get-Main $script:proc.Id
$toggle = Wait-ByName -Root $main -Name "Show a preview before installing" -TimeoutSec 10
Record "1k settings exposes the toggle" ([bool]$toggle) "found=$([bool]$toggle)"

$turnedOff = $false
if ($toggle) {
    $pattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($pattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { $pattern.Toggle() }
    Start-Sleep -Milliseconds 700
    $turnedOff = ($pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off)
}
Record "1l preview can be turned off" $turnedOff "toggle now off=$turnedOff"
Stop-Session

if ($turnedOff) {
    # Fresh session so the persisted setting takes effect and no session state interferes.
    $main = Start-Session
    Click-Start $script:proc.Id | Out-Null
    $adminInstead = Wait-ForAdmin $script:proc.Id 25
    $previewWhenOff = [bool](Find-ByName -Root (Get-Main $script:proc.Id) -Name "Install now")
    Record "1m with the preview off the run goes straight to elevation" ($adminInstead -and -not $previewWhenOff) `
        ("admin dialog={0} preview shown={1}" -f $adminInstead, $previewWhenOff)

    if ($adminInstead) {
        $main = Get-Main $script:proc.Id
        Invoke-Element (Find-ByName -Root $main -Name "Continue without admin")
        $done = Wait-ForEnds 3
        Record "1n the run completes with the preview off" $done ("END lines={0}" -f @(Get-MarkerLines | Where-Object { $_ -like 'END|*' }).Count)
    }

    # Turn the preview back on and confirm it persists.
    $main = Get-Main $script:proc.Id
    Invoke-Element (Wait-ByName -Root $main -Name "Settings" -TimeoutSec 15)
    Start-Sleep -Seconds 2
    $main = Get-Main $script:proc.Id
    $toggle2 = Wait-ByName -Root $main -Name "Show a preview before installing" -TimeoutSec 10
    $turnedOn = $false
    if ($toggle2) {
        $p2 = $toggle2.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($p2.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $p2.Toggle() }
        Start-Sleep -Milliseconds 700
        $turnedOn = ($p2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    }
    Record "1o preview turned back on" $turnedOn "toggle now on=$turnedOn"
}
Stop-Session

# ================================================================ summary
$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 1 SUMMARY: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
$results | ForEach-Object {
    if ($_.Pass) { $tag = "PASS" } else { $tag = "FAIL" }
    Write-Host ("  " + $tag + "  " + $_.Name + " - " + $_.Detail)
}
if ($failed -eq 0) { exit 0 } else { exit 1 }
