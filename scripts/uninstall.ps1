# MSC Multiplayer uninstaller: removes the mod and, if a multiplayer session was interrupted,
# puts your own save back first (the mod normally does this by itself).
param(
    [string]$GameDir,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msc-paths.ps1')

function Exit-Uninstaller([int]$Code) {
    if (-not $NoPause) {
        Write-Host ''
        Read-Host 'Press Enter to close this window' | Out-Null
    }
    exit $Code
}

# Mirrors SaveSync.RestoreOwnSave in the mod.
function Restore-OwnSave {
    $saveDir = Get-SaveDir
    $journal = Join-Path $saveDir 'MSCMP_backups\active-backup.txt'
    if (-not (Test-Path -LiteralPath $journal)) { return }
    Write-Step 'Found a backup of your own save from an unfinished multiplayer session - restoring it...'
    $lines = Get-Content -LiteralPath $journal
    if ($lines.Count -lt 2 -or -not $lines[0].StartsWith('MSCMP-BACKUP')) { throw 'the backup journal is unreadable' }
    $folder = Join-Path (Join-Path $saveDir 'MSCMP_backups') $lines[1].Trim()
    for ($i = 2; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].Trim()
        if ($line.Length -lt 3) { continue }
        $existed = $line[0] -eq '1'
        $name = $line.Substring(2)
        if ($name -match '[\\/]') { continue }
        $dst = Join-Path $saveDir $name
        $src = Join-Path $folder $name
        if ($existed -and -not (Test-Path -LiteralPath $src)) { throw "the backup copy of $name is missing (backups are in $folder)" }
        if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force }
        if ($existed) { Copy-Item -LiteralPath $src -Destination $dst -Force }
    }
    Remove-Item -LiteralPath $journal -Force
    Write-Ok 'Your own save is back.'
}

Write-Host ''
Write-Host '  MSC Multiplayer - uninstaller' -ForegroundColor Yellow
Write-Host ''
try {
    $game = Find-GameDir $GameDir
    if (-not $game) { $game = Read-GameDir }
    if (-not $game) { Write-Bad 'My Summer Car was not found.'; Exit-Uninstaller 1 }
    Wait-GameClosed
    Restore-OwnSave
    $mods = Get-ModsDir $game
    $removed = $false
    foreach ($f in @($ModFileName) + $OldModFiles) {
        foreach ($dir in @($mods, (Join-Path $mods 'References'))) {
            $p = Join-Path $dir $f
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; Write-Ok "Removed $p"; $removed = $true }
        }
    }
    if (-not $removed) { Write-Warn2 'MSC Multiplayer was not installed in this game.' }
    Write-Host ''
    Write-Host '  Done. Your settings and save backups were left in place:' -ForegroundColor Green
    Write-Host "    $(Join-Path $mods 'Config\Mod Settings\MSCMP')"
    Write-Host "    $(Join-Path (Get-SaveDir) 'MSCMP_backups')"
    Exit-Uninstaller 0
} catch {
    Write-Bad "Uninstall failed: $($_.Exception.Message)"
    Exit-Uninstaller 1
}
