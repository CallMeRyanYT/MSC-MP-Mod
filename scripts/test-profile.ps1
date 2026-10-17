[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Restore', 'Status')][string]$Action = 'Status',
    [string]$ProfilePath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Amistech\My Summer Car'),
    [string]$JournalPath,
    [switch]$SteamCloudDisabled,
    [switch]$SkipPlayerPrefs
)
$ErrorActionPreference = 'Stop'
if ([String]::IsNullOrEmpty($JournalPath)) { $JournalPath = Join-Path $PSScriptRoot '..\.local\test-profile\journal.json' }
$registrySubKey = 'Software\Amistech\My Summer Car'

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
function Write-Journal($Journal) {
    $parent = Split-Path -Parent $JournalPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temp = $JournalPath + '.writing'
    $content = $Journal | ConvertTo-Json -Depth 12
    $stream = [IO.File]::Open($temp, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($content)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $JournalPath) { [IO.File]::Replace($temp, $JournalPath, $JournalPath + '.previous') }
    else { [IO.File]::Move($temp, $JournalPath) }
}

$ProfilePath = Get-CanonicalPath $ProfilePath
$JournalPath = Get-CanonicalPath $JournalPath
Assert-NoReparse $JournalPath
Assert-NoReparse ($JournalPath + '.writing')
Assert-NoReparse ($JournalPath + '.previous')
Assert-NoReparse ($JournalPath + '.lock')
if ($JournalPath.StartsWith($ProfilePath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery journal must be outside the profile directory.' }

if ($Action -eq 'Status') {
    if (Test-Path -LiteralPath $JournalPath) {
        $journal = Get-Content -LiteralPath $JournalPath -Raw | ConvertFrom-Json
        [pscustomobject]@{ state = $journal.state; profilePath = $journal.profilePath; backupPath = $journal.backupPath; testArchivePath = $journal.testArchivePath; originalFiles = @($journal.manifest).Count; playerPrefsCaptured = $journal.playerPrefs.captured }
    } else { [pscustomobject]@{ state = 'NoJournal'; profilePath = $ProfilePath } }
    return
}
New-Item -ItemType Directory -Path (Split-Path -Parent $JournalPath) -Force | Out-Null
$journalLock = $null
try {
try { $journalLock = [IO.File]::Open($JournalPath + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { throw 'Another profile operation holds this recovery journal. Wait for it to finish.' }
Assert-NoGame
if (!$SteamCloudDisabled) { throw 'Steam Cloud must be temporarily OFF for this game before Prepare or Restore. Pass -SteamCloudDisabled only after verifying that in Steam.' }
Assert-NoReparse $ProfilePath

if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $JournalPath) { throw 'A recovery journal already exists. Restore it before another test; archive completed journals explicitly.' }
    if (!(Test-Path -LiteralPath $ProfilePath -PathType Container)) { throw 'Expected original profile directory does not exist; refusing to guess a target.' }
    $id = [Guid]::NewGuid().ToString('N')
    $backup = $ProfilePath + '.mscmp-original-' + $id
    $archive = $ProfilePath + '.mscmp-test-' + $id
    if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $archive)) { throw 'Generated backup/archive path already exists.' }
    $prefs = if ($SkipPlayerPrefs) { [pscustomobject]@{ captured = $false; existed = $false; values = @() } } else { Get-PlayerPrefsSnapshot }
    $journal = [pscustomobject]@{ schema = 1; id = $id; state = 'Planned'; createdUtc = [DateTime]::UtcNow.ToString('o'); profilePath = $ProfilePath; backupPath = $backup; testArchivePath = $archive; manifest = @(Get-Manifest $ProfilePath); playerPrefsKey = $registrySubKey; playerPrefs = $prefs; steamCloudConfirmedDisabled = $true }
    Write-Journal $journal
    Assert-NoGame
    Move-Item -LiteralPath $ProfilePath -Destination $backup
    $journal.state = 'OriginalMoved'
    Write-Journal $journal
    New-Item -ItemType Directory -Path $ProfilePath | Out-Null
    [IO.File]::WriteAllText((Join-Path $ProfilePath '.mscmp-disposable-profile'), $id)
    $journal.state = 'Prepared'
    Write-Journal $journal
    Assert-Manifest $backup $journal.manifest
    Write-Output "Disposable profile prepared. Original files remain at $backup. Keep Steam Cloud OFF until Restore finishes."
    return
}

if (!(Test-Path -LiteralPath $JournalPath)) { throw 'Recovery journal not found.' }
$journal = Get-Content -LiteralPath $JournalPath -Raw | ConvertFrom-Json
if ($journal.schema -ne 1 -or $journal.profilePath -cne $ProfilePath -or $journal.playerPrefsKey -cne $registrySubKey -or $journal.id -notmatch '^[a-f0-9]{32}$' -or $journal.backupPath -cne ($ProfilePath + '.mscmp-original-' + $journal.id) -or $journal.testArchivePath -cne ($ProfilePath + '.mscmp-test-' + $journal.id)) { throw 'Recovery journal target validation failed.' }
Assert-NoReparse $journal.backupPath
Assert-NoReparse $journal.testArchivePath
if ($journal.state -eq 'Restored') { Assert-Manifest $ProfilePath $journal.manifest; Write-Output 'Original profile already restored and file hashes verified.'; return }
if (Test-Path -LiteralPath $journal.backupPath) {
    Assert-Manifest $journal.backupPath $journal.manifest
    if (Test-Path -LiteralPath $ProfilePath) {
        if (Test-Path -LiteralPath $journal.testArchivePath) { throw 'Both disposable profile and archive exist; preserve both and resolve manually.' }
        Move-Item -LiteralPath $ProfilePath -Destination $journal.testArchivePath
        $journal.state = 'TestArchived'
        Write-Journal $journal
    }
    Assert-NoGame
    Move-Item -LiteralPath $journal.backupPath -Destination $ProfilePath
    $journal.state = 'FilesRestored'
    Write-Journal $journal
}
Assert-Manifest $ProfilePath $journal.manifest
Restore-PlayerPrefs $journal.playerPrefs
$journal.state = 'Restored'
Write-Journal $journal
Write-Output "Original profile restored and all $(@($journal.manifest).Count) file hashes verified. Disposable data preserved at $($journal.testArchivePath). Steam Cloud can now be restored to its original setting."
} finally { if ($null -ne $journalLock) { $journalLock.Dispose() } }
