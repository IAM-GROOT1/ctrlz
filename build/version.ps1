# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1

<#
.SYNOPSIS
    Resolves the release version for a build.

.DESCRIPTION
    Precedence:
      1. an explicit -Version argument
      2. GITHUB_REF_NAME when the workflow was triggered by a tag push
      3. the newest git tag reachable from HEAD

    The value is written to stdout, so callers can capture it:

        $version = & "$PSScriptRoot/version.ps1"

    Only MAJOR.MINOR.PATCH and MAJOR.MINOR.PATCH-pre.release are accepted, so a bad tag
    fails the build instead of producing a mislabelled artifact.
#>
[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"

function Test-SemanticVersion {
    param([string]$Value)
    return $Value -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$'
}

function ConvertFrom-TagName {
    param([string]$Tag)
    if ([string]::IsNullOrWhiteSpace($Tag)) { return $null }
    $clean = $Tag.Trim()
    if ($clean.StartsWith("refs/tags/", [StringComparison]::OrdinalIgnoreCase)) {
        $clean = $clean.Substring("refs/tags/".Length)
    }
    if ($clean.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $clean = $clean.Substring(1)
    }
    return $clean
}

$candidates = [System.Collections.Generic.List[object]]::new()

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $candidates.Add([pscustomobject]@{ Source = "-Version"; Value = $Version.Trim() })
}

if ($env:GITHUB_REF_TYPE -eq 'tag' -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
    $candidates.Add([pscustomobject]@{ Source = 'GITHUB_REF_NAME'; Value = (ConvertFrom-TagName $env:GITHUB_REF_NAME) })
}

$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $root = Split-Path -Parent $PSScriptRoot
    Push-Location $root
    try {
        $tag = & $git.Source describe --tags --abbrev=0 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tag)) {
            $candidates.Add([pscustomobject]@{ Source = 'git tag'; Value = (ConvertFrom-TagName $tag) })
        }
    }
    catch {
        Write-Verbose "git describe failed: $($_.Exception.Message)"
    }
    finally {
        Pop-Location
    }
}

foreach ($candidate in $candidates) {
    if ([string]::IsNullOrWhiteSpace($candidate.Value)) { continue }
    if (Test-SemanticVersion $candidate.Value) { return $candidate.Value }
    throw "The version '$($candidate.Value)' from $($candidate.Source) is not a semantic version. Expected MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-pre.release."
}

throw "No version could be resolved. Pass -Version x.y.z, or build from a git tag such as v1.2.0."
