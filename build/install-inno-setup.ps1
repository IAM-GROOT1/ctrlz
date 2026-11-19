<#
.SYNOPSIS
    Installs the exact Inno Setup compiler that the release workflow is pinned to.

.DESCRIPTION
    Downloads one immutable GitHub release asset from jrsoftware/issrc, verifies its SHA-256
    before executing it, installs it silently, and puts ISCC.exe on PATH for later steps.

    The version and hash are pinned here on purpose: the release workflow must reproduce the
    same compiler every time, and a tampered or silently replaced installer must never be
    able to build a release. To upgrade, change BOTH values together and verify the new hash
    from https://api.github.com/repos/jrsoftware/issrc/releases (asset "digest").
#>
[CmdletBinding()]
param(
    [string]$Version = "6.7.4"
)

$ErrorActionPreference = "Stop"

# jrsoftware/issrc publishes immutable releases, so this hash cannot be replaced in place.
$pinnedSha256 = "a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c"

$tag = "is-" + ($Version -replace '\.', '_')
$url = "https://github.com/jrsoftware/issrc/releases/download/$tag/innosetup-$Version.exe"
$installer = Join-Path ([IO.Path]::GetTempPath()) "innosetup-$Version.exe"

Write-Host "Downloading Inno Setup $Version from $url"
Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing

$actual = (Get-FileHash -Path $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $pinnedSha256) {
    Remove-Item $installer -Force -ErrorAction SilentlyContinue
    throw "Inno Setup $Version failed its SHA-256 check. Expected $pinnedSha256 but got $actual. Refusing to install."
}

Write-Host "SHA-256 verified: $actual"

$install = Start-Process -FilePath $installer -PassThru -Wait -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS"
)
if ($install.ExitCode -ne 0) {
    throw "The Inno Setup installer exited with code $($install.ExitCode)."
}

$searchRoots = @()
if (${env:ProgramFiles(x86)}) { $searchRoots += ${env:ProgramFiles(x86)} }
if ($env:ProgramFiles) { $searchRoots += $env:ProgramFiles }

$iscc = $null
foreach ($root in $searchRoots) {
    $candidate = Join-Path (Join-Path $root "Inno Setup 6") "ISCC.exe"
    if (Test-Path $candidate) {
        $iscc = $candidate
        break
    }
}

if (-not $iscc) {
    throw "ISCC.exe was not found after installing Inno Setup $Version."
}

$isccDirectory = Split-Path -Parent $iscc
Write-Host "ISCC: $iscc"

# build/build-installer.ps1 resolves ISCC.exe with Get-Command, so it must be on PATH.
# GITHUB_PATH carries it to the following workflow steps; $env:PATH covers this one.
if ($env:GITHUB_PATH) {
    Add-Content -Path $env:GITHUB_PATH -Value $isccDirectory
}

$env:PATH = "$isccDirectory;$env:PATH"
Write-Host "Inno Setup $Version pinned and installed."
