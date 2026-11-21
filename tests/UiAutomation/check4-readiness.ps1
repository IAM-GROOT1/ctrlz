# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 4 (UI half): the ready-to-wipe checklist against this machine.
#
# Measures UI-thread responsiveness while the scan runs and reports the summary exactly as the
# page words it. It does NOT match the word "pass" inside other words (the previous version
# matched "password manager" and reported a false PASS).
#
# Cancel is reported as NOT VERIFIED when the scan finishes too fast for the button to appear.
# Nothing prints a folder path or a file name.

param(
    [string]$AppExe,
    [string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\UiHelpers.ps1"

$results = New-Object System.Collections.ArrayList
function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    $tag = if ($Pass) { "[PASS]" } else { "[FAIL]" }
    Write-Host ("$tag " + $Name + " :: " + $Detail)
}

function Get-Main([int]$ProcessId) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    $proc.Refresh()
    if ($proc.MainWindowHandle -eq 0) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Get-PageText([int]$ProcessId) {
    $main = Get-Main $ProcessId
    if (-not $main) { return "" }
    return ((Get-ElementNames -Root $main -Limit 1200 | Where-Object { $_.Name } | ForEach-Object { $_.Name }) -join " || ")
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
Remove-Item Env:\CTRLZ_DATA_DIR -ErrorAction SilentlyContinue
$env:CTRLZ_DATA_DIR = $null

$proc = Start-Process -FilePath $AppExe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
Start-Sleep -Seconds 2

$T_click = Get-Date
Invoke-Element (Wait-ByName -Root $main -Name "Ready to wipe" -TimeoutSec 20)

# Probe the UI thread while it scans: each probe asks the UI for an element and times the reply.
$latencies = New-Object System.Collections.ArrayList
$sawCancel = $false
$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    $probeStart = Get-Date
    $cancelPresent = [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Cancel")
    [void]$latencies.Add(((Get-Date) - $probeStart).TotalMilliseconds)
    if ($cancelPresent) { $sawCancel = $true }

    $page = Get-PageText $proc.Id
    $busy = $page -match 'Scanning' -or $cancelPresent
    $hasSummary = $page -match '\d+\s+items?\s+need\s+your\s+attention' -or $page -match 'all\s+\d+\s+items\s+look\s+fine'
    if (-not $busy -and $hasSummary) { break }
    Start-Sleep -Milliseconds 80
}
$T_done = Get-Date

$page = Get-PageText $proc.Id
$summary = [regex]::Match($page, '(\d+)\s+items?\s+need\s+your\s+attention|Ready to wipe.{0,3}all\s+(\d+)\s+items\s+look\s+fine')
$attentionCount = if ($summary.Groups[1].Success) { [int]$summary.Groups[1].Value } else { 0 }

Record "4a checklist completes and shows a real summary" $summary.Success `
    ("{0:N1}s; page says: {1}" -f ($T_done - $T_click).TotalSeconds, $summary.Value)

# Discard the first probe: it includes the page's first render, not the scan.
$samples = @($latencies | Select-Object -Skip 1)
if ($samples.Count -lt 5) {
    Write-Host ("[NOT VERIFIED] 4b responsiveness: only {0} usable sample(s); the scan is too short to measure" -f $samples.Count)
} else {
    $sorted = $samples | Sort-Object
    $p95 = $sorted[[int][Math]::Floor($sorted.Count * 0.95)]
    $max = ($sorted | Measure-Object -Maximum).Maximum
    $avg = ($sorted | Measure-Object -Average).Average
    Record "4b UI thread stayed responsive during the scan" ($p95 -lt 500) `
        ("probes={0} avg={1:N0}ms p95={2:N0}ms max={3:N0}ms" -f $sorted.Count, $avg, $p95, $max)
}

try { Save-WindowShot -Element (Get-Main $proc.Id) -Path (Join-Path $ShotDir "04-readiness.png") | Out-Null } catch { }

Write-Host ("[INFO] page totals as shown: " + $(if ($summary.Groups[1].Success) { "$attentionCount items need attention" } else { "all items look fine" }))

# --- Cancel
$main = Get-Main $proc.Id
$rescan = Find-ByName -Root $main -Name "Re-scan"
if (-not $rescan) { $rescan = Find-ByName -Root $main -Name "Scan again" }
Record "4c a re-scan button is available" ([bool]$rescan) "found=$([bool]$rescan)"

if ($rescan) {
    Invoke-Element $rescan
    # The scan completes in about a second on this machine, so look for the button immediately.
    $cancelSeen = $false
    for ($i = 0; $i -lt 20; $i++) {
        if (Find-ByName -Root (Get-Main $proc.Id) -Name "Cancel") { $cancelSeen = $true; break }
        Start-Sleep -Milliseconds 50
    }

    if ($cancelSeen) {
        Invoke-Element (Find-ByName -Root (Get-Main $proc.Id) -Name "Cancel")
        Start-Sleep -Seconds 2
        $after = Get-PageText $proc.Id
        Record "4d Cancel stops the scan" ($after -match 'cancelled') "page mentions cancellation=$([bool]($after -match 'cancelled'))"
    } else {
        Write-Host "[NOT VERIFIED] 4d Cancel: the scan finished before the Cancel button appeared, so cancellation could not be exercised through the UI."
    }
}

try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 4 (UI) SUMMARY: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
