# Shared helpers for the MSC Multiplayer scripts: finding the game, MSCLoader and the Mods folder.
# Works with Windows PowerShell 5.1 (built into Windows 10/11) and PowerShell 7.

$Script:MscAppId = 516750
$Script:ModFileName = 'MSCMP.dll'
# Files left behind by older builds of this project (they would clash with the new mod).
$Script:OldModFiles = @('MSCMP.Mod.dll', 'MSCMP.Core.dll', 'MSCMP.Network.dll', 'MSCMP.Game.dll', 'MSCMP.Sync.dll', 'MSCMP.Persistence.dll')

function Write-Step([string]$Text) { Write-Host "  > $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "  OK  $Text" -ForegroundColor Green }
function Write-Warn2([string]$Text) { Write-Host "  !   $Text" -ForegroundColor Yellow }
function Write-Bad([string]$Text) { Write-Host "  X   $Text" -ForegroundColor Red }

function Test-GameDir([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path 'mysummercar.exe')) -and (Test-Path -LiteralPath (Join-Path $Path 'mysummercar_Data'))
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $keys = @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )
    foreach ($k in $keys) {
        try {
            $v = (Get-ItemProperty -Path $k.Path -Name $k.Name -ErrorAction Stop).($k.Name)
            if ($v) {
                $p = $v -replace '/', '\'
                if ((Test-Path -LiteralPath $p) -and -not $roots.Contains($p)) { $roots.Add($p) }
            }
        } catch { }
    }
    foreach ($p in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        if ($p -and (Test-Path -LiteralPath $p) -and -not $roots.Contains($p)) { $roots.Add($p) }
    }
    return $roots
}

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($root in Get-SteamRoots) {
        if (-not $libs.Contains($root)) { $libs.Add($root) }
        foreach ($vdf in @((Join-Path $root 'steamapps\libraryfolders.vdf'), (Join-Path $root 'config\libraryfolders.vdf'))) {
            if (-not (Test-Path -LiteralPath $vdf)) { continue }
            foreach ($line in Get-Content -LiteralPath $vdf -ErrorAction SilentlyContinue) {
                # New format:  "path"  "H:\\SteamLibrary"     Old format:  "1"  "D:\\Games\\Steam"
                if ($line -match '^\s*"(path|\d+)"\s+"([^"]+)"') {
                    $p = $Matches[2] -replace '\\\\', '\'
                    if ((Test-Path -LiteralPath $p) -and -not $libs.Contains($p)) { $libs.Add($p) }
                }
            }
        }
    }
    return $libs
}

# Finds the My Summer Car folder. Returns $null if it cannot be found.
function Find-GameDir([string]$Hint) {
    if (Test-GameDir $Hint) { return (Resolve-Path -LiteralPath $Hint).Path }
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'steamapps\common\My Summer Car'
        if (Test-GameDir $candidate) { return $candidate }
    }
    return $null
}

# Asks the user for the game folder (folder picker, falling back to typing it).
function Read-GameDir {
    Write-Host ''
    Write-Warn2 'Could not find My Summer Car automatically.'
    Write-Host '      Please pick your "My Summer Car" folder (the one that contains mysummercar.exe).'
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = 'Select your My Summer Car folder (contains mysummercar.exe)'
        $dlg.ShowNewFolderButton = $false
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and (Test-GameDir $dlg.SelectedPath)) {
            return $dlg.SelectedPath
        }
    } catch { }
    for ($i = 0; $i -lt 3; $i++) {
        $typed = Read-Host '      Paste the folder path here'
        $typed = $typed.Trim().Trim('"')
        if (Test-GameDir $typed) { return $typed }
        Write-Bad 'That folder does not contain mysummercar.exe.'
    }
    return $null
}

function Test-MSCLoader([string]$GameDir) {
    return (Test-Path -LiteralPath (Join-Path $GameDir 'mysummercar_Data\Managed\MSCLoader.dll')) -and
           (Test-Path -LiteralPath (Join-Path $GameDir 'doorstop_config.ini'))
}

# Where MSCLoader loads mods from: doorstop_config.ini [MSCLoader] mods = GF | MD | AD
function Get-ModsDir([string]$GameDir) {
    $mode = 'GF'
    $ini = Join-Path $GameDir 'doorstop_config.ini'
    if (Test-Path -LiteralPath $ini) {
        foreach ($line in Get-Content -LiteralPath $ini) {
            if ($line -match '^\s*mods\s*=\s*([A-Za-z]+)') { $mode = $Matches[1].ToUpperInvariant() }
        }
    }
    switch ($mode) {
        'MD' { return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'MySummerCar\Mods') }
        'AD' { return (Join-Path (Get-UserProfileDir) 'AppData\LocalLow\Amistech\My Summer Car\Mods') }
        default { return (Join-Path $GameDir 'Mods') }
    }
}

function Get-UserProfileDir {
    if ($env:USERPROFILE) { return $env:USERPROFILE }
    return [Environment]::GetFolderPath('UserProfile')
}

# Where My Summer Car keeps its save (Application.persistentDataPath).
function Get-SaveDir { return (Join-Path (Get-UserProfileDir) 'AppData\LocalLow\Amistech\My Summer Car') }

function Test-GameRunning {
    return $null -ne (Get-Process -Name 'mysummercar' -ErrorAction SilentlyContinue)
}

function Wait-GameClosed {
    while (Test-GameRunning) {
        Write-Warn2 'My Summer Car is running. Please close the game first.'
        Read-Host '      Press Enter when the game is closed' | Out-Null
    }
}
