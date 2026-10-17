$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('.local\profile-tests\' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $fixture 'My Summer Car'
$journal = Join-Path $fixture 'journal.json'
$script = Join-Path $root 'scripts\test-profile.ps1'
New-Item -ItemType Directory -Path (Join-Path $profile 'nested') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'defaultES2File.txt'), 'original world sentinel')
[IO.File]::WriteAllText((Join-Path $profile 'nested\prefs.txt'), 'original nested sentinel')
$count = 0
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:count++ }
function Expect-Failure([scriptblock]$Body, [string]$Pattern) { try { & $Body; throw 'Expected failure did not occur.' } catch { Assert ($_.Exception.Message -match $Pattern) ('Unexpected failure: ' + $_.Exception.Message) } }
# The real -File entry point evaluates parameter defaults before PSScriptRoot is
# populated on this Windows PowerShell host. Exercise its default journal path.
$defaultCheckout = Join-Path $fixture 'default-journal'
$defaultScripts = Join-Path $defaultCheckout 'scripts'
New-Item -ItemType Directory -Path $defaultScripts -Force | Out-Null
$defaultScript = Join-Path $defaultScripts 'test-profile.ps1'
Copy-Item -LiteralPath $script -Destination $defaultScript
$defaultOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $defaultScript -Action Status -ProfilePath $profile
Assert ($LASTEXITCODE -eq 0 -and (($defaultOutput | Out-String) -match 'NoJournal')) 'Default journal path failed in actual Windows PowerShell -File entry point.'
Expect-Failure { & $script -Action Prepare -ProfilePath $profile -JournalPath $journal -SkipPlayerPrefs } 'Steam Cloud'
& $script -Action Prepare -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled -SkipPlayerPrefs | Out-Null
$state = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
Assert ($state.state -eq 'Prepared') 'Profile not prepared.'
Assert (!(Test-Path -LiteralPath (Join-Path $profile 'defaultES2File.txt'))) 'Original leaked into disposable profile.'
Assert ((Get-Content -LiteralPath (Join-Path $state.backupPath 'defaultES2File.txt') -Raw) -eq 'original world sentinel') 'Original not preserved.'
Expect-Failure { & $script -Action Prepare -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled -SkipPlayerPrefs } 'journal already exists'
[IO.File]::WriteAllText((Join-Path $profile 'defaultES2File.txt'), 'disposable world')
& $script -Action Restore -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled | Out-Null
Assert ((Get-Content -LiteralPath (Join-Path $profile 'defaultES2File.txt') -Raw) -eq 'original world sentinel') 'Original was not restored.'
Assert ((Get-Content -LiteralPath (Join-Path $state.testArchivePath 'defaultES2File.txt') -Raw) -eq 'disposable world') 'Disposable data was not archived.'
Assert ((Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).state -eq 'Restored') 'Journal not marked restored.'
& $script -Action Restore -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled | Out-Null
$count++
Expect-Failure { & $script -Action Restore -ProfilePath (Join-Path $fixture 'Wrong') -JournalPath $journal -SteamCloudDisabled } 'target validation'
[IO.File]::WriteAllText((Join-Path $profile 'defaultES2File.txt'), 'tampered sentinel')
Expect-Failure { & $script -Action Restore -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled } 'hash verification'

# Recover after the disposable directory was archived but before the original move.
$recoveryProfile = Join-Path $fixture 'recovery-profile'
$recoveryJournal = Join-Path $fixture 'recovery.json'
New-Item -ItemType Directory -Path $recoveryProfile | Out-Null
[IO.File]::WriteAllText((Join-Path $recoveryProfile 'save.txt'), 'recover me')
& $script -Action Prepare -ProfilePath $recoveryProfile -JournalPath $recoveryJournal -SteamCloudDisabled -SkipPlayerPrefs | Out-Null
$recoveryState = Get-Content -LiteralPath $recoveryJournal -Raw | ConvertFrom-Json
Move-Item -LiteralPath $recoveryProfile -Destination $recoveryState.testArchivePath
& $script -Action Restore -ProfilePath $recoveryProfile -JournalPath $recoveryJournal -SteamCloudDisabled | Out-Null
Assert ((Get-Content -LiteralPath (Join-Path $recoveryProfile 'save.txt') -Raw) -eq 'recover me') 'Interrupted move recovery failed.'

# Recover a crash after the original directory move but before the journal update.
$recoveryState.state = 'Prepared'
$recoveryState | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $recoveryJournal
& $script -Action Restore -ProfilePath $recoveryProfile -JournalPath $recoveryJournal -SteamCloudDisabled | Out-Null
Assert ((Get-Content -LiteralPath $recoveryJournal -Raw | ConvertFrom-Json).state -eq 'Restored') 'Post-move journal recovery failed.'

# Tampered backup must be preserved and restoration must fail before changing disposable data.
$tamperProfile = Join-Path $fixture 'tamper-profile'
$tamperJournal = Join-Path $fixture 'tamper.json'
New-Item -ItemType Directory -Path $tamperProfile | Out-Null
[IO.File]::WriteAllText((Join-Path $tamperProfile 'save.txt'), 'original')
& $script -Action Prepare -ProfilePath $tamperProfile -JournalPath $tamperJournal -SteamCloudDisabled -SkipPlayerPrefs | Out-Null
$tamperState = Get-Content -LiteralPath $tamperJournal -Raw | ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $tamperState.backupPath 'unexpected.txt'), 'unexpected')
Expect-Failure { & $script -Action Restore -ProfilePath $tamperProfile -JournalPath $tamperJournal -SteamCloudDisabled } 'file count'
Assert (Test-Path -LiteralPath (Join-Path $tamperProfile '.mscmp-disposable-profile')) 'Failed restore changed disposable directory.'
Expect-Failure { & $script -Action Prepare -ProfilePath $recoveryProfile -JournalPath (Join-Path $recoveryProfile 'journal.json') -SteamCloudDisabled -SkipPlayerPrefs } 'outside the profile'

# A damaged journal may not redirect a move to an unrelated path.
$tamperState.backupPath = Join-Path $fixture 'unrelated'
$tamperState | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tamperJournal
Expect-Failure { & $script -Action Restore -ProfilePath $tamperProfile -JournalPath $tamperJournal -SteamCloudDisabled } 'target validation'
# Exclusive lock prevents two Prepare/Restore instances from racing the journal.
$held = [IO.File]::Open($journal + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Expect-Failure { & $script -Action Restore -ProfilePath $profile -JournalPath $journal -SteamCloudDisabled } 'Another profile operation' }
finally { $held.Dispose() }
Write-Host "Passed $count test-profile assertions. Fixture retained at $fixture"
