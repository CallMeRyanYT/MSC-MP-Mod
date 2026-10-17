[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$built = Join-Path $script:RepositoryRoot 'artifacts\current'
$manifestPath = Join-Path $built 'build-manifest.json'
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Build first.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$assembly = Join-Path $built 'MSCMP.Mod.dll'
if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Build artifact changed. Rebuild first.' }
Assert-CurrentSources $manifest
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$staging = Join-Path $script:RepositoryRoot ('.local\package\' + $stamp)
New-Item -ItemType Directory -Path (Join-Path $staging 'Mods'), (Join-Path $staging 'docs'), (Join-Path $staging 'vendor\LiteNetLib'), (Join-Path $staging 'scripts') -Force | Out-Null
Copy-Item -LiteralPath $assembly -Destination (Join-Path $staging 'Mods\MSCMP.Mod.dll')
foreach ($name in @('README.md', 'COMPATIBILITY.md', 'THIRD_PARTY_NOTICES.md', 'ARCHITECTURE.md', 'PROTOCOL.md', 'HANDOFF.md', 'CHANGELOG.md', 'CONTRIBUTING.md')) {
    Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot $name) -Destination (Join-Path $staging $name)
}
foreach ($name in @('TESTING.md', 'FEATURE_SUPPORT.md', 'KNOWN_ISSUES.md', 'BINDINGS.md', 'PROJECT_BRIEF.md', 'TRANSPORT.md', 'WORLD_SYNC.md', 'PERSISTENCE.md', 'GAMEPLAY_EVIDENCE.md', 'CHARACTERS.md', 'PLAYER_CHECKS.md', 'FEEDBACK.md', 'ITEM_BINDING_EVIDENCE.md', 'INSTALL.md', 'RUNTIME_EVIDENCE.md')) {
    Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot ('docs\' + $name)) -Destination (Join-Path $staging ('docs\' + $name))
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging 'build-manifest.json')
foreach ($name in @('dependencies.lock.json', 'vendor\LiteNetLib\LICENSE.txt', 'vendor\LiteNetLib\PATCHES.md', 'scripts\test-profile.ps1')) {
    Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot $name) -Destination (Join-Path $staging $name)
}
$output = Join-Path $script:RepositoryRoot ('artifacts\MSC-MP-Mod-0.2.1-experimental-' + $stamp + '.zip')
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $output
Write-Host "Created diagnostic-only package: $output"
Write-Host 'Allowlist includes this mod DLL, hashes, license/patch notices, player documentation and the profile-recovery tool. No game data or personal saves.'
