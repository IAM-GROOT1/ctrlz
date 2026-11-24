# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1

param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# The git tag (or an explicit -Version) is the single source of truth for the version.
$Version = & (Join-Path $PSScriptRoot "version.ps1") -Version $Version
# AssemblyVersion and FileVersion are strictly numeric, so a pre-release suffix is dropped.
$numericVersion = ($Version -split '-')[0]

$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    (Get-Command dotnet).Source
} elseif (Test-Path (Join-Path $root ".tools/dotnet/dotnet.exe")) {
    Join-Path $root ".tools/dotnet/dotnet.exe"
} else {
    throw "The .NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 or put it on PATH."
}

$project = Join-Path $root "src/CtrlZ.App/CtrlZ.App.csproj"
$tests = Join-Path $root "tests/CtrlZ.Core.Tests/CtrlZ.Core.Tests.csproj"
$artifactRoot = Join-Path $root "artifacts"
$publishDir = Join-Path $artifactRoot "publish/portable"

Write-Host "CTRL+Z $Version - publishing win-x64 portable executable"
if (-not $SkipTests) {
    & $dotnet test $tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

# SatelliteResourceLanguages keeps the translated resources for the languages in
# src/CtrlZ.App/Localization/AppLanguage.cs (";" is written %3B for MSBuild) and drops the rest.
& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:SatelliteResourceLanguages=en%3Bar%3Bde `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion="$numericVersion.0" `
    -p:FileVersion="$numericVersion.0" `
    -p:InformationalVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# The command line (ctrlz.exe) ships next to CTRL+Z.exe and shares its data folder.
$cliProject = Join-Path $root "src/CtrlZ.Cli/CtrlZ.Cli.csproj"
& $dotnet publish $cliProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:SatelliteResourceLanguages=en `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion="$numericVersion.0" `
    -p:FileVersion="$numericVersion.0" `
    -p:InformationalVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the command line failed." }
Copy-Item (Join-Path $publishDir "ctrlz.exe") (Join-Path $artifactRoot "ctrlz-$Version.exe") -Force

Copy-Item (Join-Path $root "README.md") $publishDir -Force
Copy-Item (Join-Path $root "LICENSE") $publishDir -Force
$portableName = "CTRL+Z-Portable-$Version.exe"
Copy-Item (Join-Path $publishDir "CTRL+Z.exe") (Join-Path $artifactRoot $portableName) -Force

$exe = Get-Item (Join-Path $publishDir "CTRL+Z.exe")
$sizeMb = [math]::Round(($exe.Length / 1048576), 1)
Write-Host "Portable build: $($exe.FullName) ($sizeMb MB)"
Write-Host "Single-file copy: $(Join-Path $artifactRoot $portableName)"

