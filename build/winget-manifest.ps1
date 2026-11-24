# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Writes the winget manifests (version, installer, default locale) for a published CTRL+Z
# release into artifacts/winget/<version>, ready to validate and submit to microsoft/winget-pkgs.
# It reads the Setup file's SHA-256 from the release's SHA256SUMS.txt; nothing is uploaded.
#
#   powershell -File build/winget-manifest.ps1 -Tag v1.4.0
#   winget validate --manifest artifacts/winget/1.4.0

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Repository = "IAM-GROOT1/ctrlz",
    [string]$PackageIdentifier = "IAMGROOT1.CTRLZ",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot "artifacts\winget" }

$version = $Tag.TrimStart('v')
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Not a release tag: $Tag" }

$setupName = "CTRL+Z-Setup-$version.exe"
$base = "https://github.com/$Repository/releases/download/$Tag"
# The "+" in the file name is percent-encoded (%2B) so the URL means the same file everywhere.
$setupUrl = "$base/" + [Uri]::EscapeDataString($setupName)

$content = (Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS.txt").Content
# GitHub serves the file as application/octet-stream, which PowerShell returns as bytes.
$sums = if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content) } else { [string]$content }
$line = ($sums -split "`r?`n") | Where-Object { $_ -match "^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$" -and $Matches[2] -eq $setupName } | Select-Object -First 1
if (-not $line) { throw "$setupName is not listed in SHA256SUMS.txt of $Tag." }
$sha = ([regex]::Match($line, '^[0-9a-fA-F]{64}').Value).ToUpperInvariant()

$outDir = Join-Path $OutputRoot $version
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$schema = "1.6.0"
$header = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest"

$files = [ordered]@{
    "$PackageIdentifier.yaml" = @"
$header.version.$schema.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@
    "$PackageIdentifier.installer.yaml" = @"
$header.installer.$schema.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: user
UpgradeBehavior: install
ProductCode: '{0941C8F1-8BBA-4F3E-822A-7DDB6598644A}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $setupUrl
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@
    "$PackageIdentifier.locale.en-US.yaml" = @"
$header.defaultLocale.$schema.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $version
PackageLocale: en-US
Publisher: IAM-GROOT1
PublisherUrl: https://github.com/IAM-GROOT1
PublisherSupportUrl: https://github.com/$Repository/issues
PackageName: CTRL+Z
PackageUrl: https://github.com/$Repository
License: AGPL-3.0-only
LicenseUrl: https://github.com/$Repository/blob/main/LICENSE
ShortDescription: Rebuild your apps and browser profiles after a clean Windows install.
Description: |-
  CTRL+Z is a local-first Windows app. It scans what is installed, saves a portable profile, and
  reinstalls everything silently on a fresh Windows install, including apps winget does not carry.
  It backs up Firefox and Chromium browser profiles without passwords or cookies, checks what a
  wipe would lose before you reinstall, and has no account and no telemetry.
Tags:
- backup
- reinstall
- winget
- browser-backup
- setup
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: $schema
"@
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($name in $files.Keys) {
    $text = ($files[$name] -replace "`r`n", "`n").TrimEnd() + "`n"
    [IO.File]::WriteAllText((Join-Path $outDir $name), $text, $utf8)
}

"Wrote $($files.Count) manifests for $PackageIdentifier $version to $outDir"
"Setup SHA-256: $sha"
