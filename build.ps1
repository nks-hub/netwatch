#requires -Version 5
# Builds NetWatch in Release and stages the portable EXE into .\dist.
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$proj    = Join-Path $root 'netwatch.csproj'
$dist    = Join-Path $root 'dist'
$outDir  = Join-Path $root 'bin\Release\net48'

Write-Host "Building $proj (Release)..." -ForegroundColor Cyan
dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$exe = Join-Path $outDir 'netwatch.exe'
if (-not (Test-Path $exe)) { throw "Expected output not found: $exe" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $exe $dist -Force

$cfgSrc = Join-Path $outDir 'netwatch.exe.config'
if (Test-Path $cfgSrc) { Copy-Item $cfgSrc $dist -Force }

$distExe = Join-Path $dist 'netwatch.exe'
$sizeKb  = [math]::Round((Get-Item $distExe).Length / 1KB, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  EXE : $distExe"
Write-Host "  Size: $sizeKb KB"
