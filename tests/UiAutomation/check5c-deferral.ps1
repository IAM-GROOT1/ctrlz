# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Sub-session C: an update prompt is deferred while an install queue is running.
#
# Installs 1.2.0 per-user, points it at an isolated CTRLZ_DATA_DIR holding one synthetic app whose
# "installer" sleeps for 60 s, starts the queue, asks for an update check while it runs, and then
# asserts the prompt only appears after the queue has finished.

param(
    [Parameter(Mandatory = $true)][string]$SetupV120,
    [Parameter(Mandatory = $true)][string]$FakeInstaller,
    [Parameter(Mandatory = $true)][string]$ShotDir
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts', 'Save-Shot', 'Invoke-FailureDump') | Out-Null

$exe = Join-Path $env:LOCALAPPDATA 'Programs\CTRL+Z\CTRL+Z.exe'
$data = Join-Path $env:TEMP 'ctrlz-5c-data'
$marker = Join-Path $env:TEMP 'ctrlz-5c-installer.log'
$results = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host "[PASS] $Name :: $Detail" } else { Write-Host "[FAIL] $Name :: $Detail" }
}

# ---------------------------------------------------------------- isolated data with one fake app
Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $data, $marker -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $data 'profiles') | Out-Null
$id = [guid]::NewGuid().ToString('n')
$apps = @(@{ id = 'c1'; name = 'Slow Fake App'; source = 'localFile'; enabled = $true; localPath = $FakeInstaller })
$profile = [ordered]@{ schemaVersion = 1; id = $id; name = 'Deferral Test'; createdUtc = '2026-01-01T00:00:00+00:00'; updatedUtc = '2026-01-01T00:00:00+00:00'; apps = $apps }
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $data "profiles\Deferral-$($id.Substring(0,8)).json"), (($profile | ConvertTo-Json -Depth 8) + "`n"), $enc)
[System.IO.File]::WriteAllText((Join-Path $data 'settings.json'), ((@{ activeProfileId = $id; autoCheckForUpdates = $true; updateChannel = 'stable'; theme = 'dark' } | ConvertTo-Json) + "`n"), $enc)

$p = Start-Process -FilePath $SetupV120 -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -PassThru -Wait
Record "C1 install 1.2.0" ($p.ExitCode -eq 0) "installer exit=$($p.ExitCode)"

# The fresh install must not have a remembered skip, and must check on start.
$settingsPath = Join-Path $data 'settings.json'
$json = Get-Content $settingsPath -Raw | ConvertFrom-Json
$json | Add-Member -NotePropertyName skippedUpdateVersion -NotePropertyValue $null -Force
$json | Add-Member -NotePropertyName lastUpdateCheckUtc -NotePropertyValue '2000-01-01T00:00:00+00:00' -Force
[System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 8), $enc)

# ---------------------------------------------------------------- start the queue
$env:CTRLZ_DATA_DIR = $data
$env:FAKE_INSTALLER_MARKER = $marker
$env:FAKE_INSTALLER_SLEEP_MS = '60000'
$proc = Start-Process -FilePath $exe -PassThru
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Invoke-FailureDump -ProcessId $proc.Id -Label 'no window' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir; exit 1 }

Invoke-Element (Wait-NameIn -ProcessId $proc.Id -Name 'Install' -TimeoutSec 20)
Start-Sleep -Seconds 2
Invoke-Element (Wait-NameIn -ProcessId $proc.Id -Name 'Install everything' -TimeoutSec 20)
$installNow = Wait-NameIn -ProcessId $proc.Id -Name 'Install now' -TimeoutSec 45
if (-not $installNow) {
    Invoke-FailureDump -ProcessId $proc.Id -Label 'preview never appeared after Install everything' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir
    Record "C1b preview appears after Install everything" $false 'no preview within 45 s'
    exit 1
}
Record "C1b preview appears after Install everything" $true 'preview shown'
Invoke-Element $installNow
$admin = Wait-NameIn -ProcessId $proc.Id -Name 'Continue without admin' -TimeoutSec 30
if ($admin) { Invoke-Element $admin }

$busy = [bool](Wait-NameIn -ProcessId $proc.Id -Name 'Cancel' -TimeoutSec 30)
Record "C2 install queue is running" $busy "cancel button visible=$busy"
if (-not $busy) { Invoke-FailureDump -ProcessId $proc.Id -Label 'queue never started' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir; exit 1 }

# ---------------------------------------------------------------- ask for an update while busy
Invoke-Element (Wait-NameIn -ProcessId $proc.Id -Name 'Settings' -TimeoutSec 20)
Start-Sleep -Seconds 2
$check = Wait-NameIn -ProcessId $proc.Id -Name 'Check for updates' -TimeoutSec 20
if (-not $check) {
    $texts = (Get-Texts -ProcessId $proc.Id) -join ' || '
    Record "C3 a check-for-updates control exists" $false 'button not found'
    Invoke-FailureDump -ProcessId $proc.Id -Label 'no check-for-updates button' -LogFolder (Join-Path $data 'logs') -ShotDir $ShotDir
    exit 1
}
Record "C3 a check-for-updates control exists" $true 'button found'
Invoke-Element $check
Start-Sleep -Seconds 6

$promptWhileBusy = [bool](Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec 3 -Quiet)
$texts = (Get-Texts -ProcessId $proc.Id) -join ' || '
$deferred = $texts -match 'defer|busy|finish|installing'
Record "C4 prompt is deferred while the queue runs" ((-not $promptWhileBusy) -and $deferred) `
    "prompt shown=$promptWhileBusy busy wording seen=$deferred"
Save-Shot -ProcessId $proc.Id -Path (Join-Path $ShotDir 'deferral.png') | Out-Null

# ---------------------------------------------------------------- after the queue finishes
$deadline = (Get-Date).AddSeconds(180)
$promptAfter = $false
$next = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    $promptAfter = [bool](Wait-NameIn -ProcessId $proc.Id -Name 'Update now' -TimeoutSec 2 -Quiet)
    if ($promptAfter) { break }
    if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the deferred prompt"; $next = (Get-Date).AddSeconds(10) }
}
Record "C5 prompt appears after the queue finishes" $promptAfter "prompt shown=$promptAfter"

Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 5C: {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
