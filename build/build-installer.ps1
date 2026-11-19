# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1

param(
    [string]$Version = "1.1.3",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

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
& $iscc "/DMyAppVersion=$Version" (Join-Path $root "installer/CTRL+Z.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setup = Join-Path $artifactDir "CTRL+Z-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer was not produced." }
Write-Host ("Installer: {0}" -f $setup)

