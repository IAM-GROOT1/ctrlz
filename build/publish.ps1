param(
    [string]$Version = "1.1.2",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

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

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:InformationalVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $root "README.md") $publishDir -Force
Copy-Item (Join-Path $root "LICENSE") $publishDir -Force
$portableName = "CTRL+Z-Portable-$Version.exe"
Copy-Item (Join-Path $publishDir "CTRL+Z.exe") (Join-Path $artifactRoot $portableName) -Force

$exe = Get-Item (Join-Path $publishDir "CTRL+Z.exe")
$sizeMb = [math]::Round(($exe.Length / 1048576), 1)
Write-Host "Portable build: $($exe.FullName) ($sizeMb MB)"
Write-Host "Single-file copy: $(Join-Path $artifactRoot $portableName)"

