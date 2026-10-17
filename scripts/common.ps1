Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# An MSBuild process launched from PowerShell 7 can inherit its module search
# path when starting Windows PowerShell. Load the matching utility manifest
# explicitly so Get-FileHash resolves from the executing host, not Core's modules.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Get-LocalConfiguration {
    $configPath = Join-Path $script:RepositoryRoot 'config\local.json'
    $config = $null
    if (Test-Path -LiteralPath $configPath) {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    $game = [Environment]::GetEnvironmentVariable('MSC_GAME_DIR')
    $mods = [Environment]::GetEnvironmentVariable('MSC_MODS_DIR')
    $compiler = [Environment]::GetEnvironmentVariable('MSC_CSC_PATH')
    if (!$game -and $config -and $config.PSObject.Properties['gameDirectory']) { $game = $config.gameDirectory }
    if (!$mods -and $config -and $config.PSObject.Properties['modsDirectory']) { $mods = $config.modsDirectory }
    if (!$compiler -and $config -and $config.PSObject.Properties['compilerPath']) { $compiler = $config.compilerPath }
    if (!$game) { throw 'Set MSC_GAME_DIR or copy config/local.example.json to config/local.json and set gameDirectory.' }
    $game = [IO.Path]::GetFullPath($game)
    if (!(Test-Path -LiteralPath (Join-Path $game 'mysummercar.exe') -PathType Leaf)) { throw 'MSC_GAME_DIR does not contain mysummercar.exe.' }
    $managed = Join-Path $game 'mysummercar_Data\Managed'
    $loaderConfig = Join-Path $game 'doorstop_config.ini'
    if (!$mods -and (Test-Path -LiteralPath $loaderConfig)) {
        $ini = Get-Content -LiteralPath $loaderConfig -Raw
        if ($ini -match '(?im)^\s*mods\s*=\s*GF\s*$') { $mods = Join-Path $game 'Mods' }
    }
    if ($mods) { $mods = [IO.Path]::GetFullPath($mods) }
    if (!$compiler) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v3.5\csc.exe' }
    [pscustomobject]@{ Game = $game; Managed = $managed; Mods = $mods; Compiler = $compiler; LoaderConfig = $loaderConfig }
}

function Assert-BuildReferences($Configuration) {
    foreach ($reference in @(Get-BuildReferences $Configuration)) {
        if (!(Test-Path -LiteralPath $reference.Path -PathType Leaf)) {
            throw "Missing $($reference.Kind) reference: $($reference.File). Inspect the game/loader or .NET 3.5 development tools; no assemblies are downloaded or redistributed."
        }
    }
    if (!(Test-Path -LiteralPath $Configuration.Compiler -PathType Leaf)) {
        throw 'The .NET Framework 3.5 C# compiler is missing. Install the .NET 3.5 development tools or set MSC_CSC_PATH to a compatible compiler.'
    }
    $compilerInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Configuration.Compiler)
    if ($compilerInfo.FileMajorPart -ne 3 -or $compilerInfo.FileMinorPart -ne 5) {
        throw 'This build pins the .NET Framework 3.5 compiler. MSC_CSC_PATH must point to that compiler, not a newer compiler with a broader runtime surface.'
    }
}

function Get-BuildReferences($Configuration) {
    # MSCLoader's Action API references Microsoft's System.Core 3.5.0.0.
    # The bundled Unity System.Core 2.0.5.0 has a different public key and is
    # not a replacement compile reference. Use the official legacy framework
    # identity, as the loader itself does, and verify Mono binding in-game.
    $framework2 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v2.0.50727'
    $framework3 = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\v3.5'
    foreach ($name in @('mscorlib.dll', 'System.dll')) {
        [pscustomobject]@{ File = $name; Kind = 'framework'; Path = (Join-Path $framework2 $name) }
    }
    [pscustomobject]@{ File = 'System.Core.dll'; Kind = 'framework'; Path = (Join-Path $framework3 'System.Core.dll') }
    foreach ($name in @('UnityEngine.dll', 'MSCLoader.dll', 'PlayMaker.dll')) {
        [pscustomobject]@{ File = $name; Kind = 'game'; Path = (Join-Path $Configuration.Managed $name) }
    }
}

function Get-ReferenceArguments($Configuration) {
    Get-BuildReferences $Configuration | ForEach-Object { '/reference:' + $_.Path }
}

function Assert-GameStopped {
    if (Get-Process -Name mysummercar -ErrorAction SilentlyContinue) {
        throw 'Close My Summer Car before installing or removing this mod.'
    }
}

function Write-JsonFile($Value, [string]$Path) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Assert-CurrentSources($Manifest) {
    $current = @(Get-ProjectSources | ForEach-Object { $_.Substring($script:RepositoryRoot.Length + 1).Replace('\', '/') } | Sort-Object)
    $recorded = @($Manifest.sources | ForEach-Object { $_.file } | Sort-Object)
    if ($current.Count -ne $recorded.Count -or @(Compare-Object $current $recorded).Count -ne 0) { throw 'Source file set changed since build. Rebuild and test first.' }
    foreach ($source in $Manifest.sources) {
        $path = Join-Path $script:RepositoryRoot $source.file
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.sha256) { throw 'Sources changed since build. Rebuild and test first.' }
    }
}

function Get-ProjectSources {
    $sourceDirectories = @((Join-Path $script:RepositoryRoot 'src'), (Join-Path $script:RepositoryRoot 'vendor\LiteNetLib'))
    foreach ($directory in $sourceDirectories) {
        if (Test-Path -LiteralPath $directory) {
            Get-ChildItem -LiteralPath $directory -Filter '*.cs' -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
                Sort-Object FullName | ForEach-Object { $_.FullName }
        }
    }
}
