# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Sub-session A: the installed build updates itself through the UI.
#
#   -Phase Main    install 1.2.0, wait for the 1.3.0 prompt, click Update now, verify the
#                  version actually advances and the log records verify then apply.
#   -Phase Skip    reinstall 1.2.0, click "Skip this version", restart, assert no prompt.
#   -Phase Remind  reinstall 1.2.0, click "Remind me later", restart, assert no prompt.
#
# Uses the real v1.2.0 and v1.3.0 releases only. Nothing here is shipped: the project is not in
# the solution and no build script references tests/.

param(
    [Parameter(Mandatory = $true)][string]$SetupV120,
    [Parameter(Mandatory = $true)][string]$ShotDir,
    [ValidateSet('Main', 'Skip', 'Remind')][string]$Phase = 'Main',
    [string]$ExpectedEndVersion = '1.3.0',
    [string]$ExpectedStartVersion = '1.2.0',
    [int]$PromptTimeoutSec = 120
)

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
. "$PSScriptRoot\lib.ps1"
Assert-Helpers -Names @('Wait-AppWindow', 'Wait-NameIn', 'Invoke-Element', 'Get-Texts', 'Save-Shot', 'Invoke-FailureDump') | Out-Null

$exe = Join-Path $env:LOCALAPPDATA 'Programs\CTRL+Z\CTRL+Z.exe'
$logFolder = Join-Path $env:LOCALAPPDATA 'CTRL+Z\logs'
$results = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail) {
    [void]$results.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Detail = $Detail })
    if ($Pass) { Write-Host "[PASS] $Name :: $Detail" } else { Write-Host "[FAIL] $Name :: $Detail" }
}

function Reset-UpdateState {
    # Reinstalling the app does not touch %LOCALAPPDATA%\CTRL+Z, so a "skip" from a previous phase
    # would suppress the next phase's prompt. Clear the remembered skip and move the last check far
    # into the past so this run always checks again.
    $settingsPath = Join-Path $env:LOCALAPPDATA 'CTRL+Z\settings.json'
    if (-not (Test-Path $settingsPath)) { return }
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName skippedUpdateVersion -NotePropertyValue $null -Force
    $json | Add-Member -NotePropertyName lastUpdateCheckUtc -NotePropertyValue '2000-01-01T00:00:00+00:00' -Force
    $json | Add-Member -NotePropertyName autoCheckForUpdates -NotePropertyValue $true -Force
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  reset update state (cleared remembered skip, moved last check into the past)"
}

function Install-Version([string]$Setup) {
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
    $p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -PassThru -Wait
    if ($p.ExitCode -eq 0) { Reset-UpdateState }
    return $p.ExitCode
}

function Get-InstalledVersion {
    if (-not (Test-Path $exe)) { return $null }
    return (Get-Item $exe).VersionInfo.FileVersion
}

function Start-App {
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    return (Start-Process -FilePath $exe -PassThru)
}

function Save-PromptShots([int]$ProcessId) {
    New-Item -ItemType Directory -Force $ShotDir | Out-Null
    $ok = Save-Shot -ProcessId $ProcessId -Path (Join-Path $ShotDir "prompt-$Phase.png")
    Write-Host "  screenshot saved: $ok"
}

function Wait-Prompt([int]$ProcessId, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $next = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $button = Wait-NameIn -ProcessId $ProcessId -Name 'Update now' -TimeoutSec 1 -Quiet
        if ($button) { return $true }
        if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the update prompt"; $next = (Get-Date).AddSeconds(10) }
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $false }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

# ---------------------------------------------------------------- install 1.2.0
Write-Host "phase: $Phase"
$exit = Install-Version $SetupV120
Record "$Phase install $ExpectedStartVersion" ($exit -eq 0 -and (Get-InstalledVersion) -like "$ExpectedStartVersion*") "installer exit=$exit version=$(Get-InstalledVersion)"

$proc = Start-App
$main = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 60
if (-not $main) { Invoke-FailureDump -ProcessId $proc.Id -Label 'main window never appeared' -LogFolder $logFolder -ShotDir $ShotDir; exit 1 }

$prompted = Wait-Prompt -ProcessId $proc.Id -TimeoutSec $PromptTimeoutSec
if (-not $prompted) {
    Invoke-FailureDump -ProcessId $proc.Id -Label 'update prompt never appeared' -LogFolder $logFolder -ShotDir $ShotDir
    Record "$Phase prompt appears" $false "no prompt within $PromptTimeoutSec s"
    exit 1
}
Save-PromptShots -ProcessId $proc.Id

$texts = (Get-Texts -ProcessId $proc.Id) -join ' || '
$hasVersion = $texts -match '1\.3\.0'
Record "$Phase prompt names 1.3.0" $hasVersion "prompt text mentions 1.3.0=$hasVersion"

$buttons = @('Update now', 'Skip this version', 'Remind me later')
$missingButtons = @($buttons | Where-Object { -not (Wait-NameIn -ProcessId $proc.Id -Name $_ -TimeoutSec 3 -Quiet) })
Record "$Phase prompt has all three buttons" ($missingButtons.Count -eq 0) "missing: $(if ($missingButtons.Count) { $missingButtons -join ', ' } else { 'none' })"

# ---------------------------------------------------------------- act on the prompt
$choice = switch ($Phase) {
    'Main' { 'Update now' }
    'Skip' { 'Skip this version' }
    default { 'Remind me later' }
}

$button = Wait-NameIn -ProcessId $proc.Id -Name $choice -TimeoutSec 10
Invoke-Element $button
Write-Host "  clicked '$choice'"

if ($Phase -ne 'Main') {
    # The prompt must not come back on the next start.
    Start-Sleep -Seconds 5
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
    $again = Start-App
    Wait-AppWindow -ProcessId $again.Id -TimeoutSec 60 | Out-Null
    $rePrompted = Wait-Prompt -ProcessId $again.Id -TimeoutSec 45
    Record "$Phase no prompt after restart" (-not $rePrompted) "prompted again=$rePrompted"
    Get-Process -Name 'CTRL+Z' -ErrorAction SilentlyContinue | Stop-Process -Force
} else {
    # Progress should be observable before the app closes.
    $sawProgress = $false
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { break }
        $t = (Get-Texts -ProcessId $proc.Id) -join ' '
        if ($t -match 'Download|Verif|Apply|Updat|%') { $sawProgress = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Record "$Phase progress advances" $sawProgress "progress text observed=$sawProgress"

    $closed = $false
    $deadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $closed = $true; break }
        Start-Sleep -Milliseconds 700
    }
    Record "$Phase app closes to apply" $closed "old process exited=$closed"

    # The apply happens asynchronously after the old process exits, so poll for the new version
    # rather than sampling once.
    $version = Get-InstalledVersion
    $deadline = (Get-Date).AddSeconds(90)
    $next = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and $version -notlike "$ExpectedEndVersion*") {
        Start-Sleep -Milliseconds 700
        $version = Get-InstalledVersion
        if ((Get-Date) -gt $next) { Write-Host "  ... waiting for the new exe (now $version)"; $next = (Get-Date).AddSeconds(10) }
    }
    Record "$Phase exe is now $ExpectedEndVersion" ($version -like "$ExpectedEndVersion*") "installed version=$version"

    $log = (Get-RedactedLogTail -LogFolder $logFolder -Lines 400) -join "`n"
    Record "$Phase log records verification" ($log -match 'verif') "log mentions verify=$([bool]($log -match 'verif'))"
    Record "$Phase log records apply" ($log -match 'appl') "log mentions apply=$([bool]($log -match 'appl'))"
}

$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ""
Write-Host ("CHECK 5A ($Phase): {0}/{1} passed" -f ($results.Count - $failed), $results.Count)
if ($failed -eq 0) { exit 0 } else { exit 1 }
