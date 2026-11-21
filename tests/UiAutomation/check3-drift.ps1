# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Check 3: profile drift, end to end through the UI.
#
# Every run deletes the data folder and rebuilds the synthetic profile from scratch, so results
# never depend on what a previous run left behind.
#
# Fixture entries are chosen to exercise each matching path:
#   exact      - 7-Zip by package id (an app that is really installed)
#   fuzzy      - a longer name for the same app ("7-Zip File Manager")
#   ambiguous  - a generic Visual C++ name; several variants are installed and normalise alike,
#                so the analyzer must report Uncertain rather than pick one
#   unknown    - a local-file source with no version at all
#   custom     - a GitHub source
#   missing    - a name nothing matches

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

# ---------------------------------------------------------------- fresh fixture every run
function New-DriftFixture {
    if (Test-Path $DataDir) { Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force (Join-Path $DataDir "profiles") | Out-Null

    $id = [guid]::NewGuid().ToString("n")
    $apps = @(
        @{ id = "d-exact"; name = "7-Zip 26.06 (x64 edition)"; source = "winget"; enabled = $true; packageId = "7zip.7zip"; version = "26.06" },
        @{ id = "d-fuzzy"; name = "7-Zip File Manager"; source = "winget"; enabled = $true },
        @{ id = "d-ambig"; name = "Microsoft Visual C++ Redistributable"; source = "winget"; enabled = $true },
        @{ id = "d-unknown"; name = "Local Only App"; source = "localFile"; enabled = $true; localPath = "C:\not-here-9f3\setup.exe" },
        @{ id = "d-github"; name = "GitHub Source App"; source = "github"; enabled = $true; gitHubRepo = "IAM-GROOT1/ctrlz"; gitHubAsset = "CtrlZ.exe" },
        @{ id = "d-missing"; name = "Definitely Not Installed App"; source = "winget"; enabled = $true; packageId = "Fake.NotInstalled.XYZ" }
    )

    $profile = [ordered]@{
        schemaVersion = 1; id = $id; name = "Drift Test"; description = "synthetic"
        createdUtc = "2026-01-01T00:00:00+00:00"; updatedUtc = "2026-01-01T00:00:00+00:00"; apps = $apps
    }
    $profilePath = Join-Path $DataDir ("profiles\DriftTest-" + $id.Substring(0, 8) + ".json")
    [System.IO.File]::WriteAllText($profilePath, (($profile | ConvertTo-Json -Depth 8) + "`n"), (New-Object System.Text.UTF8Encoding($false)))

    $settings = [ordered]@{ activeProfileId = $id; showInstallPreview = $true; autoCheckForUpdates = $false; theme = "dark" }
    [System.IO.File]::WriteAllText((Join-Path $DataDir "settings.json"), (($settings | ConvertTo-Json) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    return $apps.Count
}

function Get-DriftText([int]$ProcessId) {
    $drift = Get-WindowByTitle -ProcessId $ProcessId -Title "Profile drift" -TimeoutSec 5
    if (-not $drift) { return "" }
    return ((Get-ElementNames -Root $drift -Limit 900 | Where-Object { $_.Name } | ForEach-Object { $_.Name }) -join " || ")
}

New-Item -ItemType Directory -Force $ShotDir | Out-Null
$appCount = New-DriftFixture
Write-Host "fresh fixture: $appCount apps, data folder rebuilt"

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

$loaded = Wait-Until { (Get-DriftText $proc.Id) -match 'missing' } -TimeoutSec 120
Start-Sleep -Seconds 3
$text = Get-DriftText $proc.Id

$summary = [regex]::Match($text, '(\d+)\s+missing\s*.\s*(\d+)\s+not in profile\s*.\s*(\d+)\s+outdated')
Record "3b drift completes with a summary" ($loaded -and $summary.Success) `
    ("summary: " + $(if ($summary.Success) { "$($summary.Groups[1].Value) missing / $($summary.Groups[2].Value) extra / $($summary.Groups[3].Value) outdated" } else { "not parseable" }))

$missingCount = if ($summary.Success) { [int]$summary.Groups[1].Value } else { -1 }

# --- exact match: 7-Zip must not be missing
Record "3c exact package-id match" (-not ($text -match '7-Zip 26\.06')) "7-Zip absent from the drift list"

# --- fuzzy match: a longer name for the same app should still match
Record "3d fuzzy name match" (-not ($text -match '7-Zip File Manager')) "fuzzy entry matched, not listed as missing"

# --- ambiguous: must be Uncertain, and the row must say so
$ambiguous = $text -match 'Ambiguous'
Record "3e ambiguous pair reported as Uncertain" $ambiguous "drift text mentions Ambiguous=$ambiguous"
Record "3f ambiguous app is not silently picked" (-not ($text -match 'Exact match')) "no Exact-match label on the ambiguous entry"

# --- unknown version app, custom sources, fabricated app all show as missing
Record "3g unknown-version custom source is listed as missing" ($text -match 'Local Only App') "Local Only App present"
Record "3h GitHub source is listed as missing" ($text -match 'GitHub Source App') "GitHub Source App present"
Record "3i fabricated app is listed as missing" ($text -match 'Definitely Not Installed App') "fabricated app present"
Record "3j missing count matches the entries that should be missing" ($missingCount -ge 3) "missing=$missingCount"

try { Save-WindowShot -Element (Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 5) -Path (Join-Path $ShotDir "03-drift.png") | Out-Null } catch { }

# --- row-level Install hand-off, scoped to the drift window
$drift = Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 10
$installButton = $null
$all = $drift.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($element in $all) {
    if ($element.Current.Name -eq "Install" -and $element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { $installButton = $element; break }
}
Record "3k drift row exposes an Install action" ([bool]$installButton) "row-level Install button found=$([bool]$installButton)"

if ($installButton) {
    Invoke-Element $installButton
    $handedOff = Wait-Until { -not [bool](Get-WindowByTitle -ProcessId $proc.Id -Title "Profile drift" -TimeoutSec 2) } -TimeoutSec 15
    $onInstall = [bool](Find-ByName -Root (Get-Main $proc.Id) -Name "Install everything")
    Record "3l Install hands off to the install queue" ($handedOff -and $onInstall) "drift closed=$handedOff install page=$onInstall"
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
