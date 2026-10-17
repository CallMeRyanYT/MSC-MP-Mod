[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Integration tests for the filesystem tools only. Every script is copied into
# a disposable fixture checkout; no real game, save or deployment ledger is used.
$toolingRepository = Split-Path -Parent $PSScriptRoot
$toolingFixtureBase = [IO.Path]::GetFullPath((Join-Path $toolingRepository '.local\tooling-tests'))
$toolingFixture = [IO.Path]::GetFullPath((Join-Path $toolingFixtureBase ([Guid]::NewGuid().ToString('N'))))
$toolingPrefix = $toolingFixtureBase + [IO.Path]::DirectorySeparatorChar
if (!$toolingFixture.StartsWith($toolingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Tooling fixture must stay within this checkout .local/tooling-tests directory.'
}
$script:ToolingPassed = 0
$script:ToolingCases = New-Object 'System.Collections.Generic.List[string]'

function Assert-ToolingTrue($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-ToolingTree([string]$Root) {
    $records = @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length).Replace('\', '/')
        if ($_.PSIsContainer) { 'D ' + $relative }
        else { 'F ' + $relative + ' ' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return [String]::Join("`n", $records)
}

function Assert-ToolingUnchanged([string]$Before) {
    Assert-ToolingTrue ((Get-ToolingTree $toolingFixture) -ceq $Before) 'A refused or WhatIf operation changed fixture files/directories.'
}

function Invoke-FixtureTool([string]$Name, [hashtable]$Arguments = @{}) {
    # Verify the redirection immediately before every mutation-capable tool.
    Assert-ToolingTrue ($env:MSC_GAME_DIR -ceq $toolingGame) 'Game environment escaped the fixture.'
    Assert-ToolingTrue ($env:MSC_MODS_DIR -ceq $toolingMods) 'Mods environment escaped the fixture.'
    & (Join-Path $toolingFixture ('scripts\' + $Name)) @Arguments 6>$null | Out-Null
}

function Assert-ToolingRefused([scriptblock]$Action, [string]$MessagePattern) {
    $observed = $false
    $message = $null
    try { & $Action }
    catch { $observed = $true; $message = $_.Exception.Message }
    Assert-ToolingTrue $observed 'The operation unexpectedly succeeded instead of refusing unsafe input.'
    if ($MessagePattern) {
        Assert-ToolingTrue ($message -match $MessagePattern) ('Unexpected refusal reason: ' + $message)
    }
}

function Invoke-ToolingCase([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:ToolingPassed++
        $script:ToolingCases.Add($Name)
        Write-Host ('PASS tooling: ' + $Name)
    }
    catch {
        throw ('FAIL tooling: ' + $Name + ': ' + $_.Exception.Message + ' Fixture retained at ' + $toolingFixture)
    }
}

function Set-FixtureBuild([string]$Content) {
    [IO.File]::WriteAllText($toolingArtifact, $Content)
    $references = @('UnityEngine.dll', 'MSCLoader.dll', 'PlayMaker.dll') | ForEach-Object {
        [pscustomobject]@{
            file = $_
            kind = 'game'
            sha256 = (Get-FileHash -LiteralPath (Join-Path $toolingManaged $_) -Algorithm SHA256).Hash
        }
    }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $toolingFixture 'src\MSCMP.Mod') -Filter '*.cs' -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            file = $_.FullName.Substring($toolingFixture.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    [pscustomobject]@{
        assembly = 'MSCMP.Mod.dll'
        sha256 = (Get-FileHash -LiteralPath $toolingArtifact -Algorithm SHA256).Hash
        builtUtc = '2000-01-01T00:00:00.0000000Z'
        compilerVersion = 'fixture-only'
        target = 'filesystem-test fixture, not a loadable assembly'
        sources = $sources
        references = @($references)
        inGameVerified = $false
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $toolingManifest -Encoding UTF8
}

$toolingGame = Join-Path $toolingFixture 'fake-game'
$toolingMods = Join-Path $toolingGame 'Mods'
$toolingManaged = Join-Path $toolingGame 'mysummercar_Data\Managed'
$toolingArtifact = Join-Path $toolingFixture 'artifacts\current\MSCMP.Mod.dll'
$toolingManifest = Join-Path $toolingFixture 'artifacts\current\build-manifest.json'
$toolingTarget = Join-Path $toolingMods 'MSCMP.Mod.dll'
$toolingLedger = Join-Path $toolingFixture '.local\deployment.json'
$toolingUnrelated = Join-Path $toolingMods 'Unrelated.Mod.dll'
$toolingSave = Join-Path $toolingGame 'disposable-save-sentinel.txt'
$toolingSource = Join-Path $toolingFixture 'src\MSCMP.Mod\Fixture.cs'
$oldGameEnvironment = [Environment]::GetEnvironmentVariable('MSC_GAME_DIR', 'Process')
$oldModsEnvironment = [Environment]::GetEnvironmentVariable('MSC_MODS_DIR', 'Process')

try {
    foreach ($directory in @('scripts', 'config', 'docs', 'vendor\LiteNetLib', 'src\MSCMP.Mod', 'artifacts\current', '.local', 'fake-game\Mods', 'fake-game\mysummercar_Data\Managed')) {
        New-Item -ItemType Directory -Path (Join-Path $toolingFixture $directory) -Force | Out-Null
    }
    foreach ($name in @('common.ps1', 'deploy.ps1', 'uninstall.ps1', 'package.ps1')) {
        Copy-Item -LiteralPath (Join-Path $toolingRepository ('scripts\' + $name)) -Destination (Join-Path $toolingFixture ('scripts\' + $name))
    }
    foreach ($name in @('README.md', 'COMPATIBILITY.md', 'THIRD_PARTY_NOTICES.md', 'ARCHITECTURE.md', 'PROTOCOL.md', 'HANDOFF.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'docs\TESTING.md', 'docs\FEATURE_SUPPORT.md', 'docs\KNOWN_ISSUES.md', 'docs\BINDINGS.md', 'docs\PROJECT_BRIEF.md', 'docs\TRANSPORT.md', 'docs\WORLD_SYNC.md', 'docs\PERSISTENCE.md', 'docs\SHARED_SAVES.md', 'docs\GAMEPLAY_EVIDENCE.md', 'docs\CHARACTERS.md', 'docs\PLAYER_CHECKS.md', 'docs\FEEDBACK.md', 'docs\ITEM_BINDING_EVIDENCE.md', 'docs\SHARED_ITEM_CATALOGUE.md', 'docs\VEHICLE_BINDING_EVIDENCE.md', 'docs\INSTALL.md', 'docs\RUNTIME_EVIDENCE.md', 'dependencies.lock.json', 'vendor\LiteNetLib\LICENSE.txt', 'vendor\LiteNetLib\PATCHES.md', 'scripts\test-profile.ps1', 'scripts\shared-save.ps1')) {
        [IO.File]::WriteAllText((Join-Path $toolingFixture $name), ('Fixture document: ' + $name))
    }
    [IO.File]::WriteAllText((Join-Path $toolingGame 'mysummercar.exe'), 'Not executable. Filesystem fixture only.')
    [IO.File]::WriteAllText((Join-Path $toolingGame 'doorstop_config.ini'), "[fixture]`r`nmods=GF`r`n")
    [IO.File]::WriteAllText($toolingSource, '// Source fingerprint sentinel, not real mod code.')
    [IO.File]::WriteAllText($toolingUnrelated, 'Unrelated mod must be preserved byte for byte.')
    [IO.File]::WriteAllText($toolingSave, 'Disposable fixture save sentinel must never change.')
    foreach ($name in @('UnityEngine.dll', 'MSCLoader.dll', 'PlayMaker.dll')) {
        [IO.File]::WriteAllText((Join-Path $toolingManaged $name), ('Fake reference: ' + $name))
    }
    $unrelatedHash = (Get-FileHash -LiteralPath $toolingUnrelated -Algorithm SHA256).Hash
    $saveHash = (Get-FileHash -LiteralPath $toolingSave -Algorithm SHA256).Hash
    [Environment]::SetEnvironmentVariable('MSC_GAME_DIR', $toolingGame, 'Process')
    [Environment]::SetEnvironmentVariable('MSC_MODS_DIR', $toolingMods, 'Process')
    Set-FixtureBuild 'Owned diagnostic build one; fixture bytes only.'
    $firstHash = (Get-FileHash -LiteralPath $toolingArtifact -Algorithm SHA256).Hash

    Invoke-ToolingCase 'deploy WhatIf creates no files or directories' {
        $before = Get-ToolingTree $toolingFixture
        Invoke-FixtureTool 'deploy.ps1' @{ WhatIf = $true }
        Assert-ToolingUnchanged $before
    }

    Invoke-ToolingCase 'first deployment installs only the owned DLL' {
        Invoke-FixtureTool 'deploy.ps1'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingTarget -Algorithm SHA256).Hash -eq $firstHash) 'Installed bytes differ from the build.'
        $names = @(Get-ChildItem -LiteralPath $toolingMods -File | ForEach-Object { $_.Name } | Sort-Object)
        Assert-ToolingTrue (([String]::Join('|', $names)) -ceq 'MSCMP.Mod.dll|Unrelated.Mod.dll') 'Deployment copied unexpected files.'
        $ledger = Get-Content -LiteralPath $toolingLedger -Raw | ConvertFrom-Json
        Assert-ToolingTrue ($ledger.destination -ceq $toolingTarget -and $ledger.sha256 -eq $firstHash) 'Deployment ledger does not identify the fixture install.'
    }

    Invoke-ToolingCase 'redeployment backs up the previously owned build' {
        Set-FixtureBuild 'Owned diagnostic build two; fixture bytes only.'
        $secondHash = (Get-FileHash -LiteralPath $toolingArtifact -Algorithm SHA256).Hash
        Invoke-FixtureTool 'deploy.ps1'
        $ledger = Get-Content -LiteralPath $toolingLedger -Raw | ConvertFrom-Json
        $expectedBackup = Join-Path $toolingFixture ('.local\deployment-backups\' + $firstHash + '.dll')
        Assert-ToolingTrue ($ledger.previousBackup -ceq $expectedBackup -and $ledger.previousSha256 -eq $firstHash) 'Previous build backup was not recorded.'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $expectedBackup -Algorithm SHA256).Hash -eq $firstHash) 'Backup bytes do not match the previous build.'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingTarget -Algorithm SHA256).Hash -eq $secondHash) 'Second build was not installed.'
    }

    Invoke-ToolingCase 'rollback WhatIf preserves the current build and ledger' {
        $before = Get-ToolingTree $toolingFixture
        Invoke-FixtureTool 'uninstall.ps1' @{ RestorePrevious = $true; WhatIf = $true }
        Assert-ToolingUnchanged $before
    }

    Invoke-ToolingCase 'rollback restores the recorded previous build' {
        Invoke-FixtureTool 'uninstall.ps1' @{ RestorePrevious = $true }
        $ledger = Get-Content -LiteralPath $toolingLedger -Raw | ConvertFrom-Json
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingTarget -Algorithm SHA256).Hash -eq $firstHash) 'Rollback did not restore original bytes.'
        Assert-ToolingTrue ($ledger.sha256 -eq $firstHash -and !$ledger.previousBackup) 'Rollback ledger did not move to the restored build.'
    }

    Invoke-ToolingCase 'uninstall WhatIf changes nothing' {
        $before = Get-ToolingTree $toolingFixture
        Invoke-FixtureTool 'uninstall.ps1' @{ WhatIf = $true }
        Assert-ToolingUnchanged $before
    }

    Invoke-ToolingCase 'uninstall removes only the owned DLL and ledger' {
        Invoke-FixtureTool 'uninstall.ps1'
        Assert-ToolingTrue (!(Test-Path -LiteralPath $toolingTarget) -and !(Test-Path -LiteralPath $toolingLedger)) 'Owned DLL or deployment ledger remained after uninstall.'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingUnrelated -Algorithm SHA256).Hash -eq $unrelatedHash) 'Uninstall modified an unrelated mod.'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingSave -Algorithm SHA256).Hash -eq $saveHash) 'Uninstall modified a save sentinel.'
    }

    Invoke-ToolingCase 'an unrecorded DLL collision refuses deployment without changes' {
        [IO.File]::WriteAllText($toolingTarget, 'Unrecorded existing mod; must not be overwritten.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'without this checkout deployment record'
        Assert-ToolingUnchanged $before
        Remove-Item -LiteralPath $toolingTarget
    }

    Invoke-ToolingCase 'a modified installed DLL refuses deployment and uninstall' {
        Invoke-FixtureTool 'deploy.ps1'
        [IO.File]::WriteAllText($toolingTarget, 'Changed after deployment; user-owned modification.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'differs from this checkout deployment record'
        Assert-ToolingUnchanged $before
        Assert-ToolingRefused { Invoke-FixtureTool 'uninstall.ps1' } 'changed since deployment'
        Assert-ToolingUnchanged $before
        Copy-Item -LiteralPath $toolingArtifact -Destination $toolingTarget -Force
    }

    Invoke-ToolingCase 'a changed build artifact refuses deployment without changes' {
        $original = [IO.File]::ReadAllBytes($toolingArtifact)
        [IO.File]::WriteAllText($toolingArtifact, 'Artifact changed after manifest creation.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'does not match its manifest'
        Assert-ToolingUnchanged $before
        [IO.File]::WriteAllBytes($toolingArtifact, $original)
    }

    Invoke-ToolingCase 'a changed game reference refuses deployment without changes' {
        $reference = Join-Path $toolingManaged 'UnityEngine.dll'
        $original = [IO.File]::ReadAllBytes($reference)
        [IO.File]::WriteAllText($reference, 'Changed installed game reference.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'Game reference changed since build'
        Assert-ToolingUnchanged $before
        [IO.File]::WriteAllBytes($reference, $original)
    }

    Invoke-ToolingCase 'a locked installed DLL refuses replacement and removal' {
        $before = Get-ToolingTree $toolingFixture
        $lock = [IO.File]::Open($toolingTarget, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } $null
            Assert-ToolingRefused { Invoke-FixtureTool 'uninstall.ps1' } $null
        }
        finally { $lock.Dispose() }
        Assert-ToolingUnchanged $before
    }

    Invoke-ToolingCase 'packaging uses an exact allowlist and excludes game/save/stray artifacts' {
        [IO.File]::WriteAllText((Join-Path $toolingFixture 'artifacts\current\Assembly-CSharp.dll'), 'Must never ship.')
        [IO.File]::WriteAllText((Join-Path $toolingFixture 'artifacts\current\private.sav'), 'Must never ship.')
        [IO.File]::WriteAllText((Join-Path $toolingFixture 'artifacts\current\stray.dll'), 'Must never ship.')
        [IO.File]::WriteAllText((Join-Path $toolingFixture 'config\local.json'), '{"fixtureSecret":"must-not-ship"}')
        Invoke-FixtureTool 'package.ps1'
        $archives = @(Get-ChildItem -LiteralPath (Join-Path $toolingFixture 'artifacts') -Filter '*.zip' -File)
        Assert-ToolingTrue ($archives.Count -eq 1) 'Expected one fixture package archive.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
        try {
            $actual = @($zip.Entries | Where-Object { ![String]::IsNullOrEmpty($_.Name) } | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
            $expected = @('Mods/MSCMP.Mod.dll', 'README.md', 'COMPATIBILITY.md', 'THIRD_PARTY_NOTICES.md', 'ARCHITECTURE.md', 'PROTOCOL.md', 'HANDOFF.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'docs/TESTING.md', 'docs/FEATURE_SUPPORT.md', 'docs/KNOWN_ISSUES.md', 'docs/BINDINGS.md', 'docs/PROJECT_BRIEF.md', 'docs/TRANSPORT.md', 'docs/WORLD_SYNC.md', 'docs/PERSISTENCE.md', 'docs/SHARED_SAVES.md', 'docs/GAMEPLAY_EVIDENCE.md', 'docs/CHARACTERS.md', 'docs/PLAYER_CHECKS.md', 'docs/FEEDBACK.md', 'docs/ITEM_BINDING_EVIDENCE.md', 'docs/SHARED_ITEM_CATALOGUE.md', 'docs/VEHICLE_BINDING_EVIDENCE.md', 'docs/INSTALL.md', 'docs/RUNTIME_EVIDENCE.md', 'dependencies.lock.json', 'vendor/LiteNetLib/LICENSE.txt', 'vendor/LiteNetLib/PATCHES.md', 'scripts/test-profile.ps1', 'scripts/shared-save.ps1', 'build-manifest.json') | Sort-Object
            Assert-ToolingTrue (([String]::Join('|', $actual)) -ceq ([String]::Join('|', $expected))) ('Unexpected package entries: ' + [String]::Join(', ', $actual))
            $entry = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq 'Mods/MSCMP.Mod.dll' })[0]
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $packageHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
            Assert-ToolingTrue ($packageHash -eq (Get-FileHash -LiteralPath $toolingArtifact -Algorithm SHA256).Hash) 'Packaged mod bytes differ from the verified artifact.'
        }
        finally { $zip.Dispose() }
    }

    Invoke-ToolingCase 'packaging and deployment refuse a source changed after the build' {
        $original = [IO.File]::ReadAllBytes($toolingSource)
        [IO.File]::WriteAllText($toolingSource, '// Changed since the recorded build.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'package.ps1' } 'Sources changed since build|source.*changed|source.*match'
        Assert-ToolingUnchanged $before
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'Sources changed since build|source.*changed|source.*match'
        Assert-ToolingUnchanged $before
        [IO.File]::WriteAllBytes($toolingSource, $original)
    }

    Invoke-ToolingCase 'packaging and deployment refuse added or removed source files' {
        $added = Join-Path $toolingFixture 'src\MSCMP.Mod\AddedAfterBuild.cs'
        [IO.File]::WriteAllText($added, '// New source omitted from the build manifest.')
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'package.ps1' } 'Source file set changed since build'
        Assert-ToolingUnchanged $before
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'Source file set changed since build'
        Assert-ToolingUnchanged $before
        Remove-Item -LiteralPath $added
        $original = [IO.File]::ReadAllBytes($toolingSource)
        Remove-Item -LiteralPath $toolingSource
        $before = Get-ToolingTree $toolingFixture
        Assert-ToolingRefused { Invoke-FixtureTool 'package.ps1' } 'Source file set changed since build'
        Assert-ToolingUnchanged $before
        Assert-ToolingRefused { Invoke-FixtureTool 'deploy.ps1' } 'Source file set changed since build'
        Assert-ToolingUnchanged $before
        [IO.File]::WriteAllBytes($toolingSource, $original)
    }

    Invoke-ToolingCase 'all operations preserve unrelated mod and save sentinels' {
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingUnrelated -Algorithm SHA256).Hash -eq $unrelatedHash) 'Unrelated mod bytes changed.'
        Assert-ToolingTrue ((Get-FileHash -LiteralPath $toolingSave -Algorithm SHA256).Hash -eq $saveHash) 'Save sentinel bytes changed.'
        Assert-ToolingTrue (@(Get-ChildItem -LiteralPath $toolingMods -Filter '*.tmp' -File).Count -eq 0) 'A deployment temporary file leaked.'
    }

    [pscustomobject]@{ passed = $script:ToolingPassed; cases = @($script:ToolingCases.ToArray()); fixture = $toolingFixture; realGameAccessed = $false } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $toolingFixture 'test-results.json') -Encoding UTF8
    Write-Host ('Tooling tests passed: ' + $script:ToolingPassed + '. Isolated filesystem fixtures only; no game/runtime validation.')
}
finally {
    [Environment]::SetEnvironmentVariable('MSC_GAME_DIR', $oldGameEnvironment, 'Process')
    [Environment]::SetEnvironmentVariable('MSC_MODS_DIR', $oldModsEnvironment, 'Process')
}
