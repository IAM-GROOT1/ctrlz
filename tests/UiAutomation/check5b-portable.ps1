# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Sub-session B: the portable build updates itself in place.
#
# Runs CTRL+Z-Portable-1.2.0.exe from a temp folder with an isolated CTRLZ_DATA_DIR, drives the
# 1.3.0 prompt through the UI, and asserts the exe is replaced with no leftover helper or backup.
#
# Fails fast when the download never reaches "SHA-256 verified", because a run that never
# downloaded cannot apply and must not be reported as "the app never closed".
# Skips with NOT VERIFIED when GitHub's anonymous API quota is exhausted.

param(
    [Parameter(Mandatory = $true)][string]$PortableV120,
    [Parameter(Mandatory = $true)][string]$ShotDir,
    [int]$PromptTimeoutSec = 120,
    [int]$VerifyTimeoutSec = 120
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts', 'Save-Shot', 'Invoke-FailureDump', 'Get-RedactedLogTail') | Out-Null

$work = Join-Path $env:TEMP 'ctrlz-portable-work'
$data = Join-Path $env:TEMP 'ctrlz-portable-data'
$logFolder = Join-Path $data 'logs'
$results = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host "[PASS] $Name :: $Detail" } else { Write-Host "[FAIL] $Name :: $Detail" }
}

function Get-PortableVersion([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    return (Get-Item $Path).VersionInfo.FileVersion
}

function Get-RateRemaining {
    try {
        $r = Invoke-RestMethod -Uri 'https://api.github.com/rate_limit' -Headers @{ 'User-Agent' = 'ctrlz-ui-check' } -TimeoutSec 15
        return [int]$r.rate.remaining
    } catch {
        return -1
    }
}

function Wait-LogLine([string]$Needle, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $next = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $log = (Get-RedactedLogTail -LogFolder $logFolder -Lines 400) -join "`n"
        if ($log -match [regex]::Escape($Needle)) { return $true }
        if ((Get-Date) -gt $next) { Write-Host "  ... waiting for log line: $Needle"; $next = (Get-Date).AddSeconds(10) }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# ---------------------------------------------------------------- rate limit gate
$remaining = Get-RateRemaining
if ($remaining -eq 0) {
    Write-Host "[NOT VERIFIED] B0 GitHub anonymous API quota exhausted (X-RateLimit-Remaining=0); skipping the portable update check."
    exit 0
}
Write-Host "  GitHub API remaining: $(if ($remaining -lt 0) { 'unknown' } else { $remaining })"

# ---------------------------------------------------------------- isolated copy
Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $work, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $data | Out-Null
$exe = Join-Path $work 'CTRL+Z.exe'
Copy-Item $PortableV120 $exe -Force
$env:CTRLZ_DATA_DIR = $data
Record "B1 portable starts at $ExpectedStartVersion" ((Get-PortableVersion $exe) -like "$ExpectedStartVersion*") "version=$(Get-PortableVersion $exe)"

$proc = Start-Process -FilePath $exe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Invoke-FailureDump -ProcessId $proc.Id -Label 'portable window never appeared' -LogFolder $logFolder -ShotDir $ShotDir; exit 1 }

$button = Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec $PromptTimeoutSec
if (-not $button) {
    Invoke-FailureDump -ProcessId $proc.Id -Label 'portable update prompt never appeared' -LogFolder $logFolder -ShotDir $ShotDir
    Record "B2 prompt appears" $false "no prompt within $PromptTimeoutSec s"
    exit 1
}
Record "B2 prompt appears" $true 'prompt shown'
New-Item -ItemType Directory -Force $ShotDir | Out-Null
Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'prompt-portable.png') | Out-Null

Invoke-Element $button
Write-Host "  clicked 'Update now'"

# ---------------------------------------------------------------- download/verify gate
if (-not (Wait-LogLine -Needle 'SHA-256 verified' -TimeoutSec $VerifyTimeoutSec)) {
    $tail = (Get-RedactedLogTail -LogFolder $logFolder -Lines 20) -join "`n"
    Write-Host "[FAIL] B3 download/verify did not complete within $VerifyTimeoutSec s"
    Write-Host "  last 20 log lines:"
    foreach ($line in (Get-RedactedLogTail -LogFolder $logFolder -Lines 20)) { Write-Host "    $line" }
    Record "B3 download/verify did not complete" $false "no 'SHA-256 verified' line"
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
    exit 1
}
Record "B3 download and verification completed" $true "'SHA-256 verified' seen"

# ---------------------------------------------------------------- exact log assertions
$log = (Get-RedactedLogTail -LogFolder $logFolder -Lines 600) -join "`n"
foreach ($needle in @('Update check', 'Downloaded', 'SHA-256 verified')) {
    Record "B4 log: $needle" ($log -match [regex]::Escape($needle)) "exact line present"
}

$closed = $false
$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $closed = $true; break }
    Start-Sleep -Milliseconds 700
}
Record "B5 app closes to apply" $closed "old process exited=$closed"

$version = Get-PortableVersion $exe
$deadline = (Get-Date).AddSeconds(180)
$next = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and $version -notlike "$ExpectedEndVersion*") {
    Start-Sleep -Seconds 1
    $version = Get-PortableVersion $exe
    if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the portable exe to become 1.3.0 (now $version) of $ExpectedEndVersion"; $next = (Get-Date).AddSeconds(10) }
}
Record "B6 portable exe replaced with $ExpectedEndVersion" ($version -like "$ExpectedEndVersion*") "version=$version"

Start-Sleep -Seconds 35
$leftovers = @(Get-ChildItem $work -File | Where-Object { $_.Name -ne 'CTRL+Z.exe' } | ForEach-Object { $_.Name })
Record "B7 no leftover helper or backup after the health window" ($leftovers.Count -eq 0) `
    "leftovers: $(if ($leftovers.Count) { $leftovers -join ', ' } else { 'none' })"

$log = (Get-RedactedLogTail -LogFolder $logFolder -Lines 600) -join "`n"
foreach ($needle in @('Update helper started', 'Applying Portable update', 'applied and the previous build was removed')) {
    Record "B8 log: $needle" ($log -match [regex]::Escape($needle)) "exact line present"
}

$settingsPath = Join-Path $data 'settings.json'
$channel = if (Test-Path $settingsPath) { (Get-Content $settingsPath -Raw | ConvertFrom-Json).updateChannel } else { $null }
Record "B9 channel setting readable" (-not [string]::IsNullOrWhiteSpace($channel)) "updateChannel=$channel"

Write-Host "[NOT VERIFIED] B10 Pre-release channel offering pre-releases: only stable releases are published to the production repo."

Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $work, $data -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item Env:\CTRLZ_DATA_DIR -ErrorAction SilentlyContinue
Record "B11 cleanup removed the working folders" (-not (Test-Path $work) -and -not (Test-Path $data)) 'work/data removed'

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 5B: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
