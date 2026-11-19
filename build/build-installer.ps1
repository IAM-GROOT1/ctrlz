# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1

param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Resolve once so the installer and the published binaries always agree.
$Version = & (Join-Path $PSScriptRoot "version.ps1") -Version $Version

& (Join-Path $PSScriptRoot "publish.ps1") -Version $Version -SkipTests:$SkipTests
if ($LASTEXITCODE -ne 0) { throw "Publishing failed." }

$iscc = $null
if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) {
    $iscc = (Get-Command ISCC.exe).Source
} elseif (Test-Path (Join-Path $root ".tools/InnoSetup6/ISCC.exe")) {
    $iscc = Join-Path $root ".tools/InnoSetup6/ISCC.exe"
} elseif (Test-Path (Join-Path $root ".tools/InnoSetup/ISCC.exe")) {
    $iscc = Join-Path $root ".tools/InnoSetup/ISCC.exe"
}

if (-not $iscc) {
    throw "Inno Setup was not found. Install it from https://jrsoftware.org/isdl.php or put ISCC.exe on PATH."
}

$artifactDir = Join-Path $root "artifacts/installer"
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

# VersionInfoVersion in the .iss must be purely numeric, so a pre-release suffix is dropped
# for it. AppVersion and the output file name keep the full string, so a pre-release still
# produces CTRL+Z-Setup-<version>.exe with its suffix intact.
$numericVersion = ($Version -split '-')[0]
& $iscc "/DMyAppVersion=$Version" "/DMyAppVersionNumeric=$numericVersion" (Join-Path $root "installer/CTRL+Z.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setup = Join-Path $artifactDir "CTRL+Z-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer was not produced." }
Write-Host ("Installer: {0}" -f $setup)

