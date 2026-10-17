[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# Game-independent CI runner. This modern host is not a legacy Mono audit.
$headlessRoot = Split-Path -Parent $PSScriptRoot
$headlessFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$headlessCompiler = Join-Path $headlessFramework 'csc.exe'
if (!(Test-Path -LiteralPath $headlessCompiler)) { throw 'Windows .NET Framework 4 C# compiler is required.' }
$headlessOutput = Join-Path $headlessRoot '.local\headless-tests'
New-Item -ItemType Directory -Path $headlessOutput -Force | Out-Null
function Invoke-HeadlessSuite([string]$Name, [string[]]$Sources) {
    $executable = Join-Path $headlessOutput ($Name + '.exe')
    $arguments = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:3', '/target:exe', '/warn:4', '/warnaserror+', ('/out:' + $executable))
    foreach ($reference in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) {
        $arguments += '/reference:' + (Join-Path $headlessFramework $reference)
    }
    $arguments += $Sources
    $arguments += Join-Path $headlessRoot ('tests\' + $Name + '.cs')
    & $headlessCompiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name did not compile." }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "$Name failed." }
}
$core = @(Get-ChildItem -LiteralPath (Join-Path $headlessRoot 'src\MSCMP.Core') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$network = @(Get-ChildItem -LiteralPath (Join-Path $headlessRoot 'src\MSCMP.Network'), (Join-Path $headlessRoot 'vendor\LiteNetLib') -Filter '*.cs' -Recurse -File | ForEach-Object { $_.FullName })
$sync = @(Get-ChildItem -LiteralPath (Join-Path $headlessRoot 'src\MSCMP.Sync') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$persistence = @(Get-ChildItem -LiteralPath (Join-Path $headlessRoot 'src\MSCMP.Persistence') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
Invoke-HeadlessSuite 'SceneLifetimeTests' @((Join-Path $headlessRoot 'src\MSCMP.Mod\SceneLifetime.cs'), (Join-Path $headlessRoot 'src\MSCMP.Mod\DiagnosticTrace.cs'))
Invoke-HeadlessSuite 'AuthorityTests' $core
Invoke-HeadlessSuite 'NetworkTests' $network
Invoke-HeadlessSuite 'SyncTests' @($core + $network + $sync)
Invoke-HeadlessSuite 'AvatarRelayTests' @((Join-Path $headlessRoot 'src\MSCMP.Game\CharacterStyle.cs'), (Join-Path $headlessRoot 'src\MSCMP.Game\AvatarRelay.cs'))
Invoke-HeadlessSuite 'FeedbackBufferTests' @((Join-Path $headlessRoot 'src\MSCMP.Mod\FeedbackBuffer.cs'))
Invoke-HeadlessSuite 'FeedbackReporterTests' @((Join-Path $headlessRoot 'src\MSCMP.Mod\FeedbackBuffer.cs'), (Join-Path $headlessRoot 'src\MSCMP.Mod\FeedbackReporter.cs'))
Invoke-HeadlessSuite 'PersistenceTests' @($core + $persistence)
Write-Host 'Headless logic checks passed on .NET Framework 4. No game files, saves or deployment were used. Legacy mod/runtime validation requires scripts/test.ps1 and manual game checks.'
