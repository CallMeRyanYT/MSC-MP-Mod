# MSC Multiplayer installer.
# Double-click Install.bat (it runs this script). Both players need to do this once.
param(
    [string]$GameDir,
    [string]$DllPath,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msc-paths.ps1')

$Repo = 'CallMeRyanYT/MSC-MP-Mod'

function Exit-Installer([int]$Code) {
    if (-not $NoPause) {
        Write-Host ''
        Read-Host 'Press Enter to close this window' | Out-Null
    }
    exit $Code
}

function Find-ModDll {
    $candidates = @(
        $DllPath,
        (Join-Path $PSScriptRoot $ModFileName),
        (Join-Path $PSScriptRoot "..\$ModFileName"),
        (Join-Path $PSScriptRoot "..\build\$ModFileName"),
        (Join-Path $PSScriptRoot "..\src\MSCMP\bin\Release\net35\$ModFileName")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

function Get-LatestReleaseDll {
    Write-Step "No $ModFileName next to the installer - downloading the latest release from GitHub..."
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'User-Agent' = 'MSCMP-Installer' }
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
        $asset = $release.assets | Where-Object { $_.name -eq $ModFileName } | Select-Object -First 1
        if (-not $asset) { throw "the latest release ($($release.tag_name)) has no $ModFileName" }
        $target = Join-Path ([IO.Path]::GetTempPath()) $ModFileName
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -Headers $headers -UseBasicParsing
        Write-Ok "Downloaded $($release.tag_name)"
        return $target
    } catch {
        Write-Bad "Download failed: $($_.Exception.Message)"
        Write-Host "      Download the release zip from https://github.com/$Repo/releases and run Install.bat from it."
        return $null
    }
}

Write-Host ''
Write-Host '  ==============================================' -ForegroundColor Yellow
Write-Host '     MSC Multiplayer - installer' -ForegroundColor Yellow
Write-Host '  ==============================================' -ForegroundColor Yellow
Write-Host ''

try {
    # 1. The mod file
    $dll = Find-ModDll
    if (-not $dll) { $dll = Get-LatestReleaseDll }
    if (-not $dll) { Exit-Installer 1 }
    Write-Ok "Mod file: $dll"

    # 2. The game
    Write-Step 'Looking for My Summer Car...'
    $game = Find-GameDir $GameDir
    if (-not $game) { $game = Read-GameDir }
    if (-not $game) {
        Write-Bad 'My Summer Car was not found. Nothing was installed.'
        Exit-Installer 1
    }
    Write-Ok "Game: $game"

    # 3. MSCLoader
    if (-not (Test-MSCLoader $game)) {
        Write-Bad 'MSCLoader (the My Summer Car mod loader) is not installed.'
        Write-Host ''
        Write-Host '      MSC Multiplayer is an MSCLoader mod. Install MSCLoader first:'
        Write-Host '        1. Download it from https://www.nexusmods.com/mysummercar/mods/147'
        Write-Host '           (or https://github.com/piotrulos/MSCModLoader/releases)'
        Write-Host '        2. Unpack it somewhere (NOT inside the game folder) and run MSCLInstaller.exe'
        Write-Host '        3. Start the game once, then run this installer again.'
        Write-Host ''
        $open = Read-Host '      Open the MSCLoader download page now? (Y/N)'
        if ($open -match '^[Yy]') { Start-Process 'https://www.nexusmods.com/mysummercar/mods/147' }
        Exit-Installer 1
    }
    Write-Ok 'MSCLoader is installed'

    # 4. Game must be closed (Windows locks loaded mod files)
    Wait-GameClosed

    # 5. Mods folder
    $mods = Get-ModsDir $game
    if (-not (Test-Path -LiteralPath $mods)) { New-Item -ItemType Directory -Path $mods -Force | Out-Null }
    Write-Ok "Mods folder: $mods"

    # 6. Remove files from older builds that would clash
    foreach ($old in $OldModFiles) {
        foreach ($dir in @($mods, (Join-Path $mods 'References'))) {
            $p = Join-Path $dir $old
            if (Test-Path -LiteralPath $p) {
                Remove-Item -LiteralPath $p -Force
                Write-Ok "Removed old file $old"
            }
        }
    }

    # 7. Copy the mod
    $target = Join-Path $mods $ModFileName
    Copy-Item -LiteralPath $dll -Destination $target -Force
    try { Unblock-File -LiteralPath $target } catch { }
    $a = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($a -ne $b) { throw 'the copied file does not match - is the disk full or the folder read-only?' }
    $version = (Get-Item -LiteralPath $target).VersionInfo.ProductVersion
    Write-Ok "Installed MSC Multiplayer $version"

    Write-Host ''
    Write-Host '  All done!' -ForegroundColor Green
    Write-Host ''
    Write-Host '  How to play:'
    Write-Host '    - Your friend installs it the same way (same version).'
    Write-Host '    - Start My Summer Car from Steam. In the main menu press F2.'
    Write-Host '    - Host: click "Host game", invite your friend, press Continue.'
    Write-Host '    - Friend: accept the Steam invite. They load into your world automatically.'
    Write-Host '    - Their own save is backed up and restored automatically.'
    Write-Host ''
    Write-Host "  Full guide: https://github.com/$Repo#readme"
    Exit-Installer 0
} catch {
    Write-Host ''
    Write-Bad "Installation failed: $($_.Exception.Message)"
    Write-Host "      If this keeps happening, open an issue at https://github.com/$Repo/issues"
    Exit-Installer 1
}
