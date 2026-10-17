[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$RestorePrevious)
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
Assert-GameStopped
$ledgerPath = Join-Path $script:RepositoryRoot '.local\deployment.json'
if (!(Test-Path -LiteralPath $ledgerPath)) { throw 'No deployment record from this checkout. No files changed.' }
$ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
if (!$configuration.Mods) { throw 'Mods folder unresolved.' }
$target = Join-Path $configuration.Mods 'MSCMP.Mod.dll'
if ($ledger.destination -ne $target) { throw 'Configured Mods folder differs from deployment record. No files changed.' }
if (!(Test-Path -LiteralPath $target)) { throw 'Recorded mod DLL is missing. No files changed.' }
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $ledger.sha256) { throw 'Installed DLL changed since deployment. Refusing to remove it.' }
$handle = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$handle.Dispose()
if ($RestorePrevious) {
    $backupRoot = [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot '.local\deployment-backups')) + [IO.Path]::DirectorySeparatorChar
    if (!$ledger.previousBackup -or !([IO.Path]::GetFullPath($ledger.previousBackup).StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase))) { throw 'No valid previous build recorded.' }
    if (!(Test-Path -LiteralPath $ledger.previousBackup) -or (Get-FileHash -LiteralPath $ledger.previousBackup -Algorithm SHA256).Hash -ne $ledger.previousSha256) { throw 'Previous build backup missing or modified.' }
    if ($PSCmdlet.ShouldProcess($target, 'Restore previous diagnostic mod build')) {
        $temporary = Join-Path $configuration.Mods ('MSCMP.Mod.' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            Copy-Item -LiteralPath $ledger.previousBackup -Destination $temporary
            [IO.File]::Replace($temporary, $target, [NullString]::Value)
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        $ledger.sha256 = $ledger.previousSha256
        $ledger.previousBackup = $null
        $ledger.previousSha256 = $null
        Write-JsonFile $ledger $ledgerPath
    }
} elseif ($PSCmdlet.ShouldProcess($target, 'Remove this checkout diagnostic mod DLL only')) {
    Remove-Item -LiteralPath $target
    Remove-Item -LiteralPath $ledgerPath
}
