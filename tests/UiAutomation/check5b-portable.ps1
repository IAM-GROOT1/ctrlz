# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Sub-session B: the portable build updates itself in place.
#
# Runs CTRL+Z-Portable-1.2.0.exe from a temp folder with an isolated CTRLZ_DATA_DIR, drives the
# 1.3.0 prompt through the UI, and asserts the exe itself is replaced by 1.3.0 with no leftover
# helper or backup once the health window has passed.
#
# The channel toggle is read back from the settings file; whether Pre-release offers pre-releases
# cannot be proven while no pre-release version is published, and is reported as NOT VERIFIED.

param(
    [Parameter(Mandatory = $true)][string]$PortableV120,
    [Parameter(Mandatory = $true)][string]$ShotDir,
    [int]$PromptTimeoutSec = 120
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts', 'Save-Shot', 'Invoke-FailureDump') | Out-Null

$work = Join-Path $env:TEMP 'ctrlz-portable-work'
$data = Join-Path $env:TEMP 'ctrlz-portable-data'
$results = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host "[PASS] $Name :: $Detail" } else { Write-Host "[FAIL] $Name :: $Detail" }
}

function Get-PortableVersion([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    return (Get-Item $Path).VersionInfo.FileVersion
}

# ---------------------------------------------------------------- isolated copy
Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $work, $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $data | Out-Null
$exe = Join-Path $work 'CTRL+Z.exe'
Copy-Item $PortableV120 $exe -Force
$env:CTRLZ_DATA_DIR = $data
"portable copy: $exe version=$(Get-PortableVersion $exe)"

Record "B1 portable starts at 1.2.0" ((Get-PortableVersion $exe) -like '1.2.0*') "version=$(Get-PortableVersion $exe)"

$proc = Start-Process -FilePath $exe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Invoke-FailureDump -ProcessId $proc.Id -Label 'portable window never appeared' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir; exit 1 }

# ---------------------------------------------------------------- prompt
$button = Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec $PromptTimeoutSec
if (-not $button) {
    Invoke-FailureDump -ProcessId $proc.Id -Label 'portable update prompt never appeared' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir
    Record "B2 prompt appears" $false "no prompt within $PromptTimeoutSec s"
    exit 1
}
Record "B2 prompt appears" $true "prompt shown"
New-Item -ItemType Directory -Force $ShotDir | Out-Null
Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'prompt-portable.png') | Out-Null

$texts = (Get-Texts -ProcessId $proc.Id) -join ' || '
Record "B3 prompt names 1.3.0" ($texts -match '1\.3\.0') "mentions 1.3.0=$([bool]($texts -match '1\.3\.0'))"

Invoke-Element (Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec 10)
Write-Host "  clicked 'Update now'"

# ---------------------------------------------------------------- replacement
$closed = $false
$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $closed = $true; break }
    Start-Sleep -Milliseconds 700
}
Record "B4 app closes to apply" $closed "old process exited=$closed"

$version = Get-PortableVersion $exe
$deadline = (Get-Date).AddSeconds(180)
$next = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and $version -notlike '1.3.0*') {
    Start-Sleep -Seconds 1
    $version = Get-PortableVersion $exe
    if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the portable exe to become 1.3.0 (now $version)"; $next = (Get-Date).AddSeconds(10) }
}
Record "B5 portable exe replaced with 1.3.0" ($version -like '1.3.0*') "version=$version"

# ---------------------------------------------------------------- leftovers
Write-Host "  waiting 35 s for the health window to pass"
Start-Sleep -Seconds 35
$leftovers = @(Get-ChildItem $work -File | Where-Object { $_.Name -ne 'CTRL+Z.exe' } | ForEach-Object { $_.Name })
Record "B6 no leftover helper or backup after the health window" ($leftovers.Count -eq 0) `
    "leftovers: $(if ($leftovers.Count) { $leftovers -join ', ' } else { 'none' })"

# ---------------------------------------------------------------- channel setting
$settingsPath = Join-Path $data 'settings.json'
if (Test-Path $settingsPath) {
    $channel = (Get-Content $settingsPath -Raw | ConvertFrom-Json).updateChannel
    Record "B7 channel setting readable" (-not [string]::IsNullOrWhiteSpace($channel)) "updateChannel=$channel"
} else {
    Record "B7 channel setting readable" $false "no settings.json in the isolated data folder"
}

$log = (Get-RedactedLogTail -LogFolder (Join-Path $data 'logs') -Lines 400) -join "`n"
Record "B8 log records verification" ($log -match 'verif') "mentions verify=$([bool]($log -match 'verif'))"
Record "B9 log records apply" ($log -match 'appl') "mentions apply=$([bool]($log -match 'appl'))"

Write-Host "[NOT VERIFIED] B10 Pre-release channel offering pre-releases: only the stable v1.3.0 is published, so the two channels cannot be distinguished."

# ---------------------------------------------------------------- cleanup
# The freshly updated build may still hold its own file; stop it before deleting anything.
$alive = Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue
if ($alive) {
    $alive | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}
Remove-Item $work, $data -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item Env:\CTRLZ_DATA_DIR -ErrorAction SilentlyContinue
Record "B11 cleanup removed the working folders" (-not (Test-Path $work) -and -not (Test-Path $data)) "work/data removed"

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 5B: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
