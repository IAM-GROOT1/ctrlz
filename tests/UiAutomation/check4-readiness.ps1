# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 4 (UI half): run the ready-to-wipe checklist against this machine, measure UI-thread
# responsiveness while it scans, and verify Cancel stops it.
#
# Reports only counts, sizes, durations and status totals. Folder paths and file names are
# never printed.

param(
    [string]$AppExe,
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

function Get-Texts([int]$ProcessId) {
    $main = Get-Main $ProcessId
    if (-not $main) { return @() }
    return @(Get-ElementNames -Root $main -Limit 1200 | Where-Object { $_.Name } | ForEach-Object { $_.Name })
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
# Real data folder: no CTRLZ_DATA_DIR override.
Remove-Item Env:\CTRLZ_DATA_DIR -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2

$T_click = Get-Date
Invoke-Element (Wait-ByName -Root $main -Name "Ready to wipe" -TimeoutSec 20)

# Measure UI-thread responsiveness while the scan runs: each probe asks the UI for an element
# by name and times how long the UIA call takes to come back.
$latencies = New-Object System.Collections.ArrayList
$scanningSeen = $false
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    $probeStart = Get-Date
    $scanning = [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Cancel")
    $probeMs = ((Get-Date) - $probeStart).TotalMilliseconds
    [void]$latencies.Add($probeMs)
    if ($scanning) { $scanningSeen = $true }

    $texts = Get-Texts $proc.Id
    $joined = $texts -join ' || '
    if ($scanningSeen -and -not $scanning -and ($joined -match 'pass|warning|fail')) { break }
    Start-Sleep -Milliseconds 150
}
$T_done = Get-Date

$texts = Get-Texts $proc.Id
$joined = $texts -join ' || '
Record "4a checklist completes on the real machine" ((-not ($joined -match 'Scanning')) -and ($joined -match 'pass')) `
    ("scan duration {0:N1}s" -f ($T_done - $T_click).TotalSeconds)

$passed = ([regex]::Match($joined, '(\d+)\s+pass')).Groups[1].Value
$warned = ([regex]::Match($joined, '(\d+)\s+warning')).Groups[1].Value
$failed = ([regex]::Match($joined, '(\d+)\s+fail')).Groups[1].Value
Write-Host ("  totals: pass={0} warning={1} fail={2}" -f $passed, $warned, $failed)

$sorted = $latencies | Sort-Object
$p95 = $sorted[[int][Math]::Floor($sorted.Count * 0.95)]
$max = ($sorted | Measure-Object -Maximum).Maximum
$avg = ($sorted | Measure-Object -Average).Average
Record "4b UI thread stayed responsive during the scan" ($p95 -lt 500) `
    ("probes={0} avg={1:N0}ms p95={2:N0}ms max={3:N0}ms" -f $sorted.Count, $avg, $p95, $max)

# Sizes and counts only, never names or paths.
$sizeMatches = [regex]::Matches($joined, '(?<size>\d+(?:\.\d+)?\s?(?:B|KB|MB|GB))')
Write-Host ("  size values reported on the page: {0}" -f $sizeMatches.Count)
Write-Host ("  rows with a status glyph: {0}" -f ([regex]::Matches($joined, 'Ready to wipe|Needs attention|Cannot proceed')).Count)

try { Save-WindowShot -Element (Get-Main $proc.Id) -Path (Join-Path $ShotDir "04-readiness.png") | Out-Null } catch { }

# ---------------------------------------------------------------- cancel
$main = Get-Main $proc.Id
$rescan = Find-ByName -Root $main -Name "Re-scan"
if (-not $rescan) { $rescan = Find-ByName -Root $main -Name "Scan again" }
Record "4c a re-scan button is available" ([bool]$rescan) "found=$([bool]$rescan)"

if ($rescan) {
    Invoke-Element $rescan
    Start-Sleep -Seconds 2
    $main = Get-Main $proc.Id
    $cancel = Find-ByName -Root $main -Name "Cancel"
    Record "4d Cancel is offered while scanning" ([bool]$cancel) "found=$([bool]$cancel)"

    if ($cancel) {
        $T_cancel = Get-Date
        Invoke-Element $cancel
        $stopped = Wait-Until {
            $t = (Get-Texts $proc.Id) -join ' || '
            ($t -match 'cancelled|Cancel') -and -not [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Cancel")
        } -TimeoutSec 30
        $after = (Get-Texts $proc.Id) -join ' || '
        Record "4e Cancel stops the scan" $stopped ("stopped in {0:N1}s" -f ((Get-Date) - $T_cancel).TotalSeconds)
        Record "4f cancelled scan says so" ($after -match 'cancelled') "page mentions cancellation=$([bool]($after -match 'cancelled'))"
    }
}

try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }

$failedCount = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 4 (UI) SUMMARY: {0}/{1} passed" -f ($results.Count - $failedCount), $results.Count)
$results | ForEach-Object {
    if ($_.Pass) { $tag = "PASS" } else { $tag = "FAIL" }
    Write-Host ("  " + $tag + "  " + $_.Name + " - " + $_.Detail)
}
if ($failedCount -eq 0) { exit 0 } else { exit 1 }
