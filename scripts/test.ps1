[CmdletBinding()]
param()
# Reflection-only audit uses the installed Windows .NET Framework host, never
# executes Unity/game code and does not use the user's game process.
if ($PSVersionTable.PSVersion.Major -gt 5) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw "Test process failed (exit $LASTEXITCODE)." }
    return
}
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
Assert-BuildReferences $configuration
$outputRoot = Join-Path $script:RepositoryRoot '.local\tests'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
function Invoke-LegacyTests([string]$Name, [string[]]$Sources) {
    $executable = Join-Path $outputRoot ($Name + '.exe')
    $arguments = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:Default', '/target:exe', '/warn:4', '/warnaserror+', ('/out:' + $executable))
    $arguments += @(Get-BuildReferences $configuration | Where-Object { $_.Kind -eq 'framework' } | ForEach-Object { '/reference:' + $_.Path })
    $arguments += $Sources
    $arguments += @(Join-Path $script:RepositoryRoot ('tests\' + $Name + '.cs'))
    & $configuration.Compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name did not compile." }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "$Name failed." }
}
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'src\MSCMP.Core') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$networkSources = @(Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'src\MSCMP.Network'), (Join-Path $script:RepositoryRoot 'vendor\LiteNetLib') -Filter '*.cs' -Recurse -File | ForEach-Object { $_.FullName })
$syncSources = @(Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'src\MSCMP.Sync') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
Invoke-LegacyTests 'SceneLifetimeTests' @((Join-Path $script:RepositoryRoot 'src\MSCMP.Mod\SceneLifetime.cs'), (Join-Path $script:RepositoryRoot 'src\MSCMP.Mod\DiagnosticTrace.cs'))
Invoke-LegacyTests 'AuthorityTests' $coreSources
Invoke-LegacyTests 'NetworkTests' $networkSources
Invoke-LegacyTests 'TransportFanoutTests' $networkSources
Invoke-LegacyTests 'SyncTests' @($coreSources + $networkSources + $syncSources)
Invoke-LegacyTests 'VehicleTests' @($coreSources + $networkSources + $syncSources)
Invoke-LegacyTests 'AvatarRelayTests' @((Join-Path $script:RepositoryRoot 'src\MSCMP.Game\CharacterStyle.cs'), (Join-Path $script:RepositoryRoot 'src\MSCMP.Game\AvatarRelay.cs'))
Invoke-LegacyTests 'FeedbackBufferTests' @((Join-Path $script:RepositoryRoot 'src\MSCMP.Mod\FeedbackBuffer.cs'))
Invoke-LegacyTests 'FeedbackReporterTests' @((Join-Path $script:RepositoryRoot 'src\MSCMP.Mod\FeedbackBuffer.cs'), (Join-Path $script:RepositoryRoot 'src\MSCMP.Mod\FeedbackReporter.cs'))
$persistenceSources = @(Get-ChildItem -LiteralPath (Join-Path $script:RepositoryRoot 'src\MSCMP.Persistence') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
Invoke-LegacyTests 'PersistenceTests' @($coreSources + $persistenceSources)
$artifact = Join-Path $script:RepositoryRoot 'artifacts\current\MSCMP.Mod.dll'
$manifest = Get-Content -LiteralPath (Join-Path $script:RepositoryRoot 'artifacts\current\build-manifest.json') -Raw | ConvertFrom-Json
Assert-CurrentSources $manifest
if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Build artifact hash mismatch.' }
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($artifact)
if ($assembly.ImageRuntimeVersion -ne 'v2.0.50727') { throw 'Unexpected CLR target in compiled mod.' }
$allowed = @('mscorlib', 'System', 'System.Core', 'UnityEngine', 'MSCLoader', 'PlayMaker')
foreach ($reference in $assembly.GetReferencedAssemblies()) {
    if ($allowed -notcontains $reference.Name) { throw "Unexpected distributed dependency: $($reference.FullName)" }
    if ($reference.Name -eq 'mscorlib' -and $reference.Version.Major -ne 2) { throw 'Compiled mod requires a modern CLR.' }
    if ($reference.Name -eq 'System.Core' -and $reference.Version.ToString() -ne '3.5.0.0') { throw 'Unexpected System.Core target.' }
}
Write-Host 'PASS: compiled mod targets CLR 2 with only allowed legacy assembly dependencies.'
& pwsh.exe -NoProfile -File (Join-Path $PSScriptRoot 'test-runtime-api.ps1') -AssemblyPath $artifact -GameManaged $configuration.Managed
if ($LASTEXITCODE -ne 0) { throw 'Shipped runtime API audit failed.' }
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
foreach ($file in $scripts) {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "PowerShell parse error in $($file.Name): $($errors[0].Message)" }
}
Write-Host "PASS: $($scripts.Count) PowerShell scripts parse under Windows PowerShell 5.1."
& (Join-Path $script:RepositoryRoot 'tests\Tooling.Tests.ps1')
if (!$?) { throw 'Tooling regression tests failed.' }
& (Join-Path $script:RepositoryRoot 'tests\TestProfile.Tests.ps1')
if (!$?) { throw 'Test-profile recovery tests failed.' }
Write-JsonFile ([pscustomobject]@{ testedUtc = [DateTime]::UtcNow.ToString('o'); assemblySha256 = $manifest.sha256; lifecycle = 'passed'; authority = 'passed'; network = 'passed'; transportFanout = 'passed'; worldSync = 'passed'; vehicle = 'passed'; avatarRelay = 'passed'; feedbackBuffer = 'passed'; feedbackReporter = 'passed'; persistence = 'passed'; profileRecovery = 'passed'; legacyAssemblyAudit = 'passed'; tooling = 'passed'; inGame = 'awaiting manual test' }) (Join-Path $outputRoot 'results.json')
Write-Host 'Automated checks passed. Real game initialization/teardown and all multiplayer gates remain unverified.'
