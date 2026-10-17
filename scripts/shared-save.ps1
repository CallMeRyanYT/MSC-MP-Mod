[CmdletBinding()]
param(
    [ValidateSet('Capture','Restore','Recover')][string]$Action = 'Capture',
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [string]$ProfilePath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'),
    [string]$JournalPath = (Join-Path $PSScriptRoot '..\.local\test-profile\journal.json'),
    [switch]$SteamCloudDisabled,
    [switch]$SkipPlayerPrefs
)
$ErrorActionPreference = 'Stop'
$registrySubKey = 'Software\Amistech\My Summer Car'
# This tool handles only an already prepared disposable profile. It never writes
# the original profile journal or opens the original backup directory.
function Assert-NoGame {
    if (@(Get-Process -Name mysummercar -ErrorAction SilentlyContinue).Count) { throw 'Close every My Summer Car process before changing a test profile.' }
}
function Get-CanonicalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($full.StartsWith('\\')) { throw 'Network shares are not supported for disposable-profile moves.' }
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\') -or !$full.Contains('\')) { throw 'A drive root cannot be a profile or journal target.' }
    return $full
}
function Assert-NoReparse([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $cursor" }
        }
        $parent = Split-Path -Parent $cursor
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($entry in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Nested reparse point refused: $($entry.FullName)" }
        }
    }
}
function Get-Manifest([string]$Path) {
    $prefix = $Path.TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Path -Force -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ relativePath = $_.FullName.Substring($prefix.Length); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Assert-Manifest([string]$Path, $Manifest) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw "Original profile directory missing: $Path" }
    Assert-NoReparse $Path
    $actual = @(Get-Manifest $Path)
    if ($actual.Count -ne @($Manifest).Count) { throw 'Original profile file count does not match the journal. Nothing will be overwritten.' }
    for ($i = 0; $i -lt $actual.Count; $i++) {
        if ($actual[$i].relativePath -cne $Manifest[$i].relativePath -or $actual[$i].sha256 -ne $Manifest[$i].sha256 -or $actual[$i].bytes -ne $Manifest[$i].bytes) { throw 'Original profile hash verification failed. Nothing will be overwritten.' }
    }
}
function Get-PlayerPrefsSnapshot {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrySubKey, $false)
    if ($null -eq $key) { return [pscustomobject]@{ captured = $true; existed = $false; values = @() } }
    try {
        if ($key.GetSubKeyNames().Length) { throw 'Unexpected PlayerPrefs subkeys; refusing automatic registry handling.' }
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            $kind = $key.GetValueKind($_)
            $data = $key.GetValue($_, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary -or $kind -eq [Microsoft.Win32.RegistryValueKind]::None) { $data = [Convert]::ToBase64String([byte[]]$data) }
            [pscustomobject]@{ name = $_; kind = $kind.ToString(); data = $data }
        })
        return [pscustomobject]@{ captured = $true; existed = $true; values = $values }
    } finally { $key.Dispose() }
}
function Restore-PlayerPrefs($Snapshot) {
    if (!$Snapshot.captured) { return }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrySubKey, $true)
    if ($null -eq $key) {
        if (!$Snapshot.existed) { return }
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registrySubKey)
    }
    try {
        if ($key.GetSubKeyNames().Length) { throw 'Unexpected PlayerPrefs subkeys; refusing automatic registry restoration.' }
        $names = @($Snapshot.values | ForEach-Object { $_.name })
        foreach ($name in $key.GetValueNames()) { if ($names -cnotcontains $name) { $key.DeleteValue($name, $false) } }
        foreach ($entry in $Snapshot.values) {
            $kind = [Microsoft.Win32.RegistryValueKind][Enum]::Parse([Microsoft.Win32.RegistryValueKind], $entry.kind)
            $data = $entry.data
            switch ($entry.kind) {
                'Binary' { $data = [Convert]::FromBase64String($data) }
                'None' { $data = [Convert]::FromBase64String($data) }
                'DWord' { $data = [int]$data }
                'QWord' { $data = [long]$data }
                'MultiString' { $data = [string[]]$data }
                default { $data = [string]$data }
            }
            $key.SetValue($entry.name, $data, $kind)
        }
        $key.Flush()
    } finally { $key.Dispose() }
    if (!$Snapshot.existed) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($registrySubKey, $false) }
    $restored = Get-PlayerPrefsSnapshot
    if (($restored | ConvertTo-Json -Depth 8 -Compress) -cne ($Snapshot | ConvertTo-Json -Depth 8 -Compress)) { throw 'PlayerPrefs restoration verification failed; recovery journal retained.' }
}

function Write-AtomicJson($Value, [string]$Path) {
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 16))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, [NullString]::Value) } else { [IO.File]::Move($temp, $Path) }
}
function Assert-TreeBounded([string]$Path) {
    Assert-NoReparse $Path
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File)
    if ($files.Count -gt 1024 -or ($files | Measure-Object -Property Length -Sum).Sum -gt 536870912) { throw 'Shared save exceeds 1024 files / 512 MiB.' }
}
function Copy-Tree([string]$Source, [string]$Target) {
    Assert-TreeBounded $Source
    if (Test-Path -LiteralPath $Target) { throw 'Copy destination already exists.' }
    New-Item -ItemType Directory -Path $Target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
        $destination = Join-Path $Target $file.FullName.Substring($Source.Length + 1)
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
function Assert-Marker([string]$Path) {
    $marker = Join-Path $Path '.mscmp-disposable-profile'
    if (!(Test-Path -LiteralPath $marker) -or (Get-Item -LiteralPath $marker).Length -gt 36 -or [IO.File]::ReadAllText($marker).Trim() -cne $journal.id) { throw 'Active disposable marker does not match the prepared profile journal.' }
}
function Assert-Receipt([string]$Path) {
    $checkpoint = Join-Path $Path '.mscmp-shared\world.mscp'
    $receipt = Join-Path $Path '.mscmp-shared\native-save-receipt.txt'
    if (!(Test-Path -LiteralPath $receipt) -or (Get-Item -LiteralPath $receipt).Length -gt 256) { throw 'Host Save & Quit receipt missing. Everyone must leave the van, then the host must Save & Quit and close the game.' }
    $lines = [IO.File]::ReadAllLines($receipt)
    if ($lines.Count -ne 3 -or $lines[0] -cne 'MSCMP-NATIVE-SAVE-1' -or $lines[1] -cne (Get-FileHash -LiteralPath $checkpoint -Algorithm SHA256).Hash) { throw 'Native save receipt differs from checkpoint. Host must Save & Quit again.' }
    $saved = [DateTime]::Parse($lines[2]).ToUniversalTime()
    if ($saved -gt [DateTime]::UtcNow.AddMinutes(1)) { throw 'Invalid native save receipt time.' }
    if (!(Test-Path -LiteralPath (Join-Path $Path 'defaultES2File.txt'))) { throw 'Native world file missing.' }
}
$ProfilePath = Get-CanonicalPath $ProfilePath
$JournalPath = Get-CanonicalPath $JournalPath
$BundlePath = Get-CanonicalPath $BundlePath
foreach ($path in @($ProfilePath, $JournalPath)) { Assert-NoReparse $path }
if ($BundlePath -eq $ProfilePath -or $BundlePath.StartsWith($ProfilePath + '\', [StringComparison]::OrdinalIgnoreCase) -or $ProfilePath.StartsWith($BundlePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Bundle and profile must be separate directories.' }
if (!$SteamCloudDisabled) { throw 'Keep Steam Cloud OFF; pass -SteamCloudDisabled after verifying it.' }
if ((Get-Item -LiteralPath $JournalPath).Length -gt 4194304) { throw 'Profile journal exceeds limit.' }
$journal = Get-Content -LiteralPath $JournalPath -Raw | ConvertFrom-Json
if ($journal.schema -ne 1 -or $journal.state -cne 'Prepared' -or $journal.profilePath -cne $ProfilePath -or $journal.id -notmatch '^[a-f0-9]{32}$' -or $journal.backupPath -cne ($ProfilePath + '.mscmp-original-' + $journal.id)) { throw 'A matching Prepared disposable profile is required.' }
if (!$SkipPlayerPrefs -and !$journal.playerPrefs.captured) { throw 'Original PlayerPrefs were not protected by profile preparation; use -SkipPlayerPrefs.' }
# Never allow a bundle destination inside the original backup, even if absent.
if ($BundlePath -eq $journal.backupPath -or $BundlePath.StartsWith($journal.backupPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Original backup is not a bundle destination.' }
Assert-NoReparse $BundlePath
Assert-NoReparse ($JournalPath + '.lock')
$lock = [IO.File]::Open($JournalPath + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Assert-NoGame
    $transactionPath = $JournalPath + '.shared-restore.json'
    Assert-NoReparse $transactionPath
    $transaction = $null
    if (Test-Path -LiteralPath $transactionPath) {
        if ((Get-Item -LiteralPath $transactionPath).Length -gt 4194304) { throw 'Restore journal exceeds limit.' }
        $transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
        if ($transaction.id -notmatch '^[a-f0-9]{32}$' -or $transaction.profileId -cne $journal.id -or $transaction.backup -cne ($ProfilePath + '.mscmp-shared-before-' + $transaction.id) -or $transaction.stage -cne ($ProfilePath + '.mscmp-shared-stage-' + $transaction.id)) { throw 'Shared restore journal target validation failed.' }
        foreach ($path in @($transaction.backup, $transaction.stage, ($transaction.stage + '.failed'))) { Assert-NoReparse $path }
        if ($Action -ne 'Recover' -and $transaction.state -notin @('Complete','RolledBack')) { throw 'An interrupted shared restore exists. Run -Action Recover first.' }
    }
    if ($Action -eq 'Recover') {
        if ($null -eq $transaction -or $transaction.state -in @('Complete','RolledBack')) { Write-Output 'No interrupted shared restore.'; return }
        if (Test-Path -LiteralPath $transaction.backup) {
            Assert-Marker $transaction.backup
            if (Test-Path -LiteralPath $ProfilePath) {
                Assert-Marker $ProfilePath
                if (Test-Path -LiteralPath ($transaction.stage + '.failed')) { throw 'Recovery archive exists; preserve it and resolve manually.' }
                Assert-NoGame
                Move-Item -LiteralPath $ProfilePath -Destination ($transaction.stage + '.failed')
            }
            Assert-NoGame
            Move-Item -LiteralPath $transaction.backup -Destination $ProfilePath
        }
        Assert-Marker $ProfilePath
        # This is only the disposable preferences captured before this restore.
        Restore-PlayerPrefs $transaction.previousPrefs
        $transaction.state = 'RolledBack'; Write-AtomicJson $transaction $transactionPath
        Write-Output 'Interrupted shared restore rolled back; all staged files retained.'; return
    }
    Assert-Marker $ProfilePath
    Assert-TreeBounded $ProfilePath
    if ($Action -eq 'Capture') {
        Assert-Receipt $ProfilePath
        if (Test-Path -LiteralPath $BundlePath) { throw 'Bundle already exists. Choose a new destination.' }
        $stage = $BundlePath + '.writing-' + [Guid]::NewGuid().ToString('N')
        $before = @(Get-Manifest $ProfilePath)
        Copy-Tree $ProfilePath (Join-Path $stage 'profile')
        Assert-Manifest (Join-Path $stage 'profile') $before
        Assert-Manifest $ProfilePath $before
        $prefs = if ($SkipPlayerPrefs) { [pscustomobject]@{ captured=$false; existed=$false; values=@() } } else { Get-PlayerPrefsSnapshot }
        Write-AtomicJson ([pscustomobject]@{ schema=1; createdUtc=[DateTime]::UtcNow.ToString('o'); files=$before; playerPrefs=$prefs; scope='Host disposable native save and MSCMP checkpoint; unsupported gameplay remains unsynchronized.' }) (Join-Path $stage 'bundle.json')
        Assert-NoGame
        [IO.Directory]::Move($stage, $BundlePath)
        Write-Output "Shared-save bundle created: $BundlePath"; return
    }
    Assert-TreeBounded $BundlePath
    $manifestPath = Join-Path $BundlePath 'bundle.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 4194304) { throw 'Bundle manifest exceeds limit.' }
    $bundle = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($bundle.schema -ne 1) { throw 'Unsupported bundle schema.' }
    $source = Join-Path $BundlePath 'profile'
    Assert-Manifest $source $bundle.files
    Assert-Receipt $source
    $id = [Guid]::NewGuid().ToString('N')
    $stage = $ProfilePath + '.mscmp-shared-stage-' + $id
    $backup = $ProfilePath + '.mscmp-shared-before-' + $id
    Assert-NoReparse $stage; Assert-NoReparse $backup
    Copy-Tree $source $stage
    Assert-Manifest $stage $bundle.files
    # Keep this machine's marker so the original personal-profile recovery remains valid.
    [IO.File]::WriteAllText((Join-Path $stage '.mscmp-disposable-profile'), $journal.id)
    $prefs = if ($SkipPlayerPrefs) { [pscustomobject]@{ captured=$false; existed=$false; values=@() } } else { Get-PlayerPrefsSnapshot }
    $transaction = [pscustomobject]@{ id=$id; profileId=$journal.id; state='Prepared'; backup=$backup; stage=$stage; previousPrefs=$prefs }
    Write-AtomicJson $transaction $transactionPath
    Assert-NoGame
    Move-Item -LiteralPath $ProfilePath -Destination $backup
    Move-Item -LiteralPath $stage -Destination $ProfilePath
    if (!$SkipPlayerPrefs) { Restore-PlayerPrefs $bundle.playerPrefs }
    Assert-Marker $ProfilePath
    $transaction.state = 'Complete'; Write-AtomicJson $transaction $transactionPath
    Write-Output "Shared save restored into the disposable profile. Previous disposable files retained: $backup. Load the game, enable Resume, then Host."
} finally { $lock.Dispose() }

