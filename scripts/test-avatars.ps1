[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
$output = Join-Path $script:RepositoryRoot '.local\tests\AvatarRelayTests.exe'
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v2.0.50727'
& $configuration.Compiler '/nologo' '/noconfig' '/nostdlib+' '/langversion:Default' '/target:exe' '/warn:4' '/warnaserror+' ('/out:' + $output) ('/reference:' + (Join-Path $framework 'mscorlib.dll')) ('/reference:' + (Join-Path $framework 'System.dll')) (Join-Path $script:RepositoryRoot 'src\MSCMP.Game\CharacterStyle.cs') (Join-Path $script:RepositoryRoot 'src\MSCMP.Game\AvatarRelay.cs') (Join-Path $script:RepositoryRoot 'tests\AvatarRelayTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Avatar relay test compile failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Avatar relay tests failed.' }
