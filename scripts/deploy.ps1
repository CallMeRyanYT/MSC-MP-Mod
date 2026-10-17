[CmdletBinding(SupportsShouldProcess = $true)]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
Assert-GameStopped
if (!$configuration.Mods -or !(Test-Path -LiteralPath $configuration.Mods -PathType Container)) { throw 'Mods folder unresolved. Verify MSCLoader configuration and set MSC_MODS_DIR if needed.' }
$source = Join-Path $script:RepositoryRoot 'artifacts\current\MSCMP.Mod.dll'
$manifestPath = Join-Path $script:RepositoryRoot 'artifacts\current\build-manifest.json'
if (!(Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath $manifestPath)) { throw 'Run scripts/build.ps1 before deployment.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($hash -ne $manifest.sha256) { throw 'Build output does not match its manifest. Rebuild before deployment.' }
Assert-CurrentSources $manifest
foreach ($reference in $manifest.references) {
    if ($reference.kind -ne 'game') { continue }
    $localReference = Join-Path $configuration.Managed $reference.file
    if (!(Test-Path -LiteralPath $localReference) -or (Get-FileHash -LiteralPath $localReference -Algorithm SHA256).Hash -ne $reference.sha256) { throw "Game reference changed since build: $($reference.file). Reinspect and rebuild." }
}
$target = Join-Path $configuration.Mods 'MSCMP.Mod.dll'
$ledgerPath = Join-Path $script:RepositoryRoot '.local\deployment.json'
$previousHash = $null
$backup = $null
if (Test-Path -LiteralPath $target) {
    if (!(Test-Path -LiteralPath $ledgerPath)) { throw 'MSCMP.Mod.dll already exists without this checkout deployment record. Preserve/inspect it before deployment; it will not be overwritten.' }
    $previous = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
    $previousHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($previous.destination -ne $target -or $previous.sha256 -ne $previousHash) { throw 'Installed DLL differs from this checkout deployment record. Refusing to overwrite it.' }
    $handle = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $handle.Dispose()
}
if (!$PSCmdlet.ShouldProcess($target, 'Install this diagnostic mod only')) { return }
$local = Join-Path $script:RepositoryRoot '.local'
New-Item -ItemType Directory -Path $local -Force | Out-Null
if ($previousHash) {
    $backupRoot = Join-Path $local 'deployment-backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backup = Join-Path $backupRoot ($previousHash + '.dll')
    if (!(Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $target -Destination $backup }
}
$temporary = Join-Path $configuration.Mods ('MSCMP.Mod.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Copy-Item -LiteralPath $source -Destination $temporary
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $hash) { throw 'Staged copy hash mismatch.' }
    Assert-GameStopped
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $previousHash) { throw 'Destination changed during deployment.' }
        [IO.File]::Replace($temporary, $target, [NullString]::Value)
    } else { [IO.File]::Move($temporary, $target) }
    Write-JsonFile ([pscustomobject]@{ destination = $target; sha256 = $hash; installedUtc = [DateTime]::UtcNow.ToString('o'); previousBackup = $backup; previousSha256 = $previousHash }) $ledgerPath
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host 'Installed MSCMP.Mod.dll. No game assemblies, other mods, configuration, or saves were replaced.'
Write-Host 'Experimental shared cup/van adapters are installed; actual in-game multiplayer validation is still pending.'
