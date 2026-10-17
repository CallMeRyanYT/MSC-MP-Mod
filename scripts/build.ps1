[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
Assert-BuildReferences $configuration
$sources = @(Get-ProjectSources | Sort-Object)
if (!$sources.Count) { throw 'No mod source files found.' }
$staging = Join-Path $script:RepositoryRoot ('.local\build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$assemblyPath = Join-Path $staging 'MSCMP.Mod.dll'
# The 3.5 compiler calls C# 3 its Default language (numeric "3" is not accepted).
$arguments = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:Default', '/target:library', '/platform:anycpu', '/optimize+', '/warn:4', '/warnaserror+', ('/out:' + $assemblyPath))
$arguments += @(Get-ReferenceArguments $configuration)
$arguments += $sources
& $configuration.Compiler @arguments
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $assemblyPath)) { throw "Mod compilation failed (exit $LASTEXITCODE). Previous artifacts were not changed." }
$references = Get-BuildReferences $configuration | ForEach-Object {
    [pscustomobject]@{ file = $_.File; kind = $_.Kind; identity = [Reflection.AssemblyName]::GetAssemblyName($_.Path).FullName; sha256 = (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash }
}
$sourceHashes = @($sources | ForEach-Object { [pscustomobject]@{ file = $_.Substring($script:RepositoryRoot.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$metadata = [pscustomobject]@{ assembly = 'MSCMP.Mod.dll'; sha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash; builtUtc = [DateTime]::UtcNow.ToString('o'); compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($configuration.Compiler).FileVersion; target = 'Unity legacy .NET 3.5 / C# 3'; sources = $sourceHashes; references = @($references); inGameVerified = $false }
Write-JsonFile $metadata (Join-Path $staging 'build-manifest.json')
$destination = Join-Path $script:RepositoryRoot 'artifacts\current'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $destination 'MSCMP.Mod.dll') -Force
Copy-Item -LiteralPath (Join-Path $staging 'build-manifest.json') -Destination (Join-Path $destination 'build-manifest.json') -Force
Write-Host "Built $destination\MSCMP.Mod.dll against local game APIs and .NET Framework 3.5 references."
Write-Host 'Build success does not establish in-game compatibility or multiplayer support.'
