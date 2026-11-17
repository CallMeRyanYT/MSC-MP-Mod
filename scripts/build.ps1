# Builds MSCMP.dll from source against your own game files and checks it with the API audit.
#   .\scripts\build.ps1               build into .\build\MSCMP.dll
#   .\scripts\build.ps1 -Install      build and install into the game
#   .\scripts\build.ps1 -GameDir "D:\Games\My Summer Car"
param(
    [string]$GameDir,
    [switch]$Install,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msc-paths.ps1')
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Bad 'The .NET SDK is not installed. Get the .NET 8 SDK from https://dotnet.microsoft.com/download'
    exit 1
}

# Game folder: parameter > config/local.json > Steam libraries
if (-not $GameDir) {
    $local = Join-Path $root 'config\local.json'
    if (Test-Path -LiteralPath $local) {
        try { $GameDir = (Get-Content -LiteralPath $local -Raw | ConvertFrom-Json).gameDir } catch { }
    }
}
$game = Find-GameDir $GameDir
if (-not $game) { Write-Bad 'My Summer Car not found. Pass -GameDir or create config\local.json (see config\local.example.json).'; exit 1 }
if (-not (Test-MSCLoader $game)) { Write-Bad "MSCLoader is not installed in $game"; exit 1 }
Write-Ok "Game: $game"

$managed = Join-Path $game 'mysummercar_Data\Managed'
$proj = Join-Path $root 'src\MSCMP\MSCMP.csproj'
Write-Step "Building ($Configuration)..."
& dotnet build $proj -c $Configuration "-p:GameDir=$game" --nologo
if ($LASTEXITCODE -ne 0) { Write-Bad 'Build failed.'; exit 1 }

$dll = Join-Path $root "src\MSCMP\bin\$Configuration\net35\MSCMP.dll"
Write-Step 'Checking every game API the mod uses exists in your game (API audit)...'
& dotnet run --project (Join-Path $root 'tools\ApiAudit\ApiAudit.csproj') -c Release --nologo -- $dll $managed
if ($LASTEXITCODE -ne 0) { Write-Bad 'API audit failed - the mod would crash in this game version.'; exit 1 }

$out = Join-Path $root 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $out 'MSCMP.dll') -Force
Write-Ok "Built $(Join-Path $out 'MSCMP.dll')"

if ($Install) {
    & (Join-Path $PSScriptRoot 'install.ps1') -GameDir $game -DllPath (Join-Path $out 'MSCMP.dll') -NoPause
}
