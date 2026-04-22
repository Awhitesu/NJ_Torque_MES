Param(
  [string]$OutputRoot = ".\\release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$deployDir = Join-Path $repoRoot ("release\\NJ_Torque_MES_Deploy_" + $timestamp)
$publishDir = Join-Path $deployDir "app"

Write-Host "[1/5] Build frontend..."
Push-Location $repoRoot
npm run build | Out-Host
Pop-Location

Write-Host "[2/5] Publish backend (self-contained win-x64)..."
dotnet publish `
  (Join-Path $repoRoot "backend\\MesScanner.Backend\\MesScanner.Backend.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:PublishTrimmed=false `
  -o $publishDir | Out-Host

Write-Host "[3/5] Copy frontend dist -> wwwroot..."
$wwwroot = Join-Path $publishDir "wwwroot"
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item (Join-Path $repoRoot "dist\\*") $wwwroot -Recurse -Force

Write-Host "[4/5] Copy Config..."
$cfgDir = Join-Path $publishDir "Config"
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
Copy-Item (Join-Path $repoRoot "Config\\app-config.json") $cfgDir -Force

Write-Host "[5/5] Write startup scripts..."
$startBat = @"
@echo off
setlocal
cd /d %~dp0
set ASPNETCORE_URLS=http://127.0.0.1:5246
start "" http://127.0.0.1:5246
MesScanner.Backend.exe
endlocal
"@
Set-Content -Path (Join-Path $publishDir "start_app.bat") -Value $startBat -Encoding Ascii

$readme = @"
NJ_Torque_MES Deploy Guide

1) Double-click start_app.bat
2) Browser opens: http://127.0.0.1:5246
3) Runtime config: .\\Config\\app-config.json

Notes:
- This package is self-contained win-x64 and does not require installing .NET runtime.
- /api/*, /mes-api/*, /mes-push/* targets are all sourced from Config\\app-config.json.
"@
Set-Content -Path (Join-Path $deployDir "DEPLOY_README.txt") -Value $readme -Encoding UTF8

Write-Host "Done. Deploy directory: $deployDir"
