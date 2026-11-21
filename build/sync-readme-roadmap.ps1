# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Regenerates the "What's coming" block in README.md from the section list in ROADMAP.md, so the
# two can never drift apart. Run with -Check in CI to fail when the README is stale.

[CmdletBinding()]
param(
    [string]$RoadmapPath,
    [string]$ReadmePath,
    [switch]$Check
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $RoadmapPath) { $RoadmapPath = Join-Path $repoRoot "ROADMAP.md" }
if (-not $ReadmePath) { $ReadmePath = Join-Path $repoRoot "README.md" }

$startMarker = "<!-- roadmap:start -->"
$endMarker = "<!-- roadmap:end -->"

if (-not (Test-Path $RoadmapPath)) { throw "Roadmap not found: $RoadmapPath" }
if (-not (Test-Path $ReadmePath)) { throw "README not found: $ReadmePath" }

# Section lines look like: - [x] **1. Title** - done, shipping in v1.3.0
$sectionPattern = '^- \[(?<checked>x| )\] \*\*(?<number>\d+)\.\s*(?<title>.+?)\*\*(?<rest>.*)$'

$sections = @()
foreach ($line in Get-Content $RoadmapPath) {
    $match = [regex]::Match($line, $sectionPattern)
    if (-not $match.Success) { continue }

    $checked = if ($match.Groups['checked'].Value -eq 'x') { 'x' } else { ' ' }
    $number = $match.Groups['number'].Value
    $title = $match.Groups['title'].Value.Trim()
    $rest = $match.Groups['rest'].Value

    $suffix = ''
    if ($rest -match '\*\*next\*\*') {
        $suffix = ' - **next**'
    }

    $sections += "- [$checked] **$number.** $title$suffix"
}

if ($sections.Count -eq 0) {
    throw "No section lines found in $RoadmapPath. Expected lines like: - [x] **1. Title** - done"
}

$block = @(
    $startMarker
    "<!-- Generated from ROADMAP.md by build/sync-readme-roadmap.ps1. Do not edit by hand. -->"
    ''
) + $sections + @(
    ''
    'See [ROADMAP.md](ROADMAP.md) for the detail behind each section.'
    $endMarker
)

$generated = ($block -join "`n")

$readme = Get-Content $ReadmePath -Raw
$startIndex = $readme.IndexOf($startMarker, [StringComparison]::Ordinal)
$endIndex = $readme.IndexOf($endMarker, [StringComparison]::Ordinal)

if ($startIndex -lt 0 -or $endIndex -lt 0 -or $endIndex -lt $startIndex) {
    throw "README.md must contain '$startMarker' and '$endMarker' around the roadmap block."
}

$before = $readme.Substring(0, $startIndex)
$after = $readme.Substring($endIndex + $endMarker.Length)
$updated = $before + $generated + $after

if ($Check) {
    if ($updated -ne $readme) {
        Write-Host "README.md is out of date with ROADMAP.md." -ForegroundColor Red
        Write-Host "Run: pwsh -File build/sync-readme-roadmap.ps1" -ForegroundColor Yellow
        exit 1
    }

    Write-Host "README roadmap block is in sync with ROADMAP.md ($($sections.Count) sections)."
    exit 0
}

if ($updated -eq $readme) {
    Write-Host "README roadmap block already up to date ($($sections.Count) sections)."
    exit 0
}

# Write without a BOM and with LF, matching the rest of the repository.
$normalized = $updated -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($ReadmePath, $normalized, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Updated README roadmap block ($($sections.Count) sections)."
