# Builds the mod and creates the release zip in .\artifacts (what players download).
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
& (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { exit 1 }

$dll = Join-Path $root 'build\MSCMP.dll'
$version = (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion
$stage = Join-Path $root "artifacts\MSCMP-$version"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item -LiteralPath $dll -Destination $stage
foreach ($f in @('Install.bat', 'Uninstall.bat')) { Copy-Item -LiteralPath (Join-Path $root $f) -Destination $stage }
foreach ($f in @('install.ps1', 'uninstall.ps1', 'msc-paths.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $root 'docs\RELEASE_README.txt') -Destination (Join-Path $stage 'README.txt')

$zip = Join-Path $root "artifacts\MSCMP-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Release package: $zip" -ForegroundColor Green
