[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$configuration = Get-LocalConfiguration
$local = Join-Path $script:RepositoryRoot '.local'
New-Item -ItemType Directory -Path $local -Force | Out-Null
$files = @('mysummercar.exe', 'mysummercar_Data\Mono\mono.dll', 'mysummercar_Data\Managed\mscorlib.dll', 'mysummercar_Data\Managed\System.dll', 'mysummercar_Data\Managed\System.Core.dll', 'mysummercar_Data\Managed\UnityEngine.dll', 'mysummercar_Data\Managed\MSCLoader.dll', 'mysummercar_Data\Managed\MSCLoader.Preloader.dll', 'mysummercar_Data\Managed\PlayMaker.dll', 'mysummercar_Data\Managed\Assembly-CSharp.dll', 'mysummercar_Data\Managed\Assembly-CSharp-firstpass.dll', 'mysummercar_Data\Managed\Assembly-UnityScript.dll', 'mysummercar_Data\Managed\Assembly-UnityScript-firstpass.dll', 'mysummercar_Data\Managed\ES2.dll')
$dataDirectory = Join-Path $configuration.Game 'mysummercar_Data'
$files += @(Get-ChildItem -LiteralPath $dataDirectory -File | Where-Object { $_.Name -eq 'mainData' -or $_.Name -match '^level\d+$|^sharedassets\d+\.(assets|resource)$|^resources\.(assets|resource)$' } | ForEach-Object { 'mysummercar_Data\' + $_.Name })
$evidence = foreach ($relative in $files) {
    $path = Join-Path $configuration.Game $relative
    if (!(Test-Path -LiteralPath $path)) { [pscustomobject]@{ file = $relative; missing = $true }; continue }
    $assembly = $null
    if ([IO.Path]::GetExtension($path) -in @('.dll', '.exe')) {
        try { $assembly = [Reflection.AssemblyName]::GetAssemblyName($path).FullName } catch [BadImageFormatException] { }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    [pscustomobject]@{ file = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; assembly = $assembly; fileVersion = $version.FileVersion; productVersion = $version.ProductVersion; bytes = (Get-Item -LiteralPath $path).Length }
}
$manifestPath = Join-Path (Split-Path -Parent (Split-Path -Parent $configuration.Game)) 'appmanifest_516750.acf'
$manifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw } else { '' }
$buildId = if ($manifest -match '"buildid"\s+"([0-9]+)"') { $Matches[1] } else { 'unverified' }
$mods = @()
if ($configuration.Mods -and (Test-Path -LiteralPath $configuration.Mods)) {
    $mods = @(Get-ChildItem -LiteralPath $configuration.Mods -File -Filter '*.dll' | ForEach-Object {
        [pscustomobject]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$report = [pscustomobject]@{ capturedUtc = [DateTime]::UtcNow.ToString('o'); gameDirectory = $configuration.Game; modsDirectory = $configuration.Mods; steamBuildId = $buildId; compiler = $configuration.Compiler; files = @($evidence); installedMods = $mods; runtimeVerified = $false }
$output = Join-Path $local 'discovery.json'
Write-JsonFile $report $output
Write-Host "Saved local compatibility evidence: $output"
Write-Host "Steam build: $buildId; mod DLLs: $($mods.Count). Runtime execution is not verified by this script."
