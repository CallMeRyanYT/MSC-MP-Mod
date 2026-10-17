$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('.local\shared-save-tests\' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $fixture 'profile'
$journalPath = Join-Path $fixture 'journal.json'
$bundle = Join-Path $fixture 'bundle'
$tool = Join-Path $root 'scripts\shared-save.ps1'
$prepare = Join-Path $root 'scripts\test-profile.ps1'
$count = 0
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:count++ }
function Reject([scriptblock]$Body, [string]$Pattern) { try { & $Body; throw 'Expected refusal.' } catch { Assert ($_.Exception.Message -match $Pattern) ('Wrong refusal: ' + $_.Exception.Message) } }
function Invoke-Save([string]$Action, [string]$Target) { & $tool -Action $Action -BundlePath $Target -ProfilePath $profile -JournalPath $journalPath -SteamCloudDisabled -SkipPlayerPrefs | Out-Null }
New-Item -ItemType Directory -Path $profile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'personal.txt'), 'personal sentinel')
& $prepare -Action Prepare -ProfilePath $profile -JournalPath $journalPath -SteamCloudDisabled -SkipPlayerPrefs | Out-Null
$journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
$journalHash = (Get-FileHash -LiteralPath $journalPath).Hash
$personalHash = (Get-FileHash -LiteralPath (Join-Path $journal.backupPath 'personal.txt')).Hash
[IO.File]::WriteAllText((Join-Path $profile 'defaultES2File.txt'), 'host native world')
New-Item -ItemType Directory -Path (Join-Path $profile '.mscmp-shared') | Out-Null
$checkpoint = Join-Path $profile '.mscmp-shared\world.mscp'
[IO.File]::WriteAllBytes($checkpoint, [byte[]]@(1,2,3,4))
[IO.File]::WriteAllBytes((Join-Path $profile '.mscmp-shared\session-generation.mscg'), [byte[]]@(9,8,7))
Reject { Invoke-Save Capture $bundle } 'receipt missing'
$receipt = "MSCMP-NATIVE-SAVE-1`n$((Get-FileHash -LiteralPath $checkpoint).Hash)`n$([DateTime]::UtcNow.ToString('o'))`n"
[IO.File]::WriteAllText((Join-Path $profile '.mscmp-shared\native-save-receipt.txt'), $receipt)
Invoke-Save Capture $bundle
Assert (Test-Path -LiteralPath (Join-Path $bundle 'bundle.json')) 'Complete bundle missing.'
Assert ((Get-Content -LiteralPath (Join-Path $bundle 'profile\defaultES2File.txt') -Raw) -ceq 'host native world') 'Native world not captured.'
Reject { Invoke-Save Capture $bundle } 'already exists'
Reject { Invoke-Save Capture (Join-Path $profile 'nested') } 'separate directories'
Reject { Invoke-Save Capture (Join-Path $journal.backupPath 'forbidden') } 'Original backup'
[IO.File]::WriteAllText((Join-Path $profile 'defaultES2File.txt'), 'new disposable world')
Invoke-Save Restore $bundle
Assert ((Get-Content -LiteralPath (Join-Path $profile 'defaultES2File.txt') -Raw) -ceq 'host native world') 'Native restore mismatch.'
Assert ((Get-Content -LiteralPath (Join-Path $profile '.mscmp-disposable-profile') -Raw) -ceq $journal.id) 'Local marker changed.'
$transactionPath = $journalPath + '.shared-restore.json'
$transaction = Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json
Assert ((Get-Content -LiteralPath (Join-Path $transaction.backup 'defaultES2File.txt') -Raw) -ceq 'new disposable world') 'Previous disposable world not preserved.'
Assert ((Get-FileHash -LiteralPath $journalPath).Hash -eq $journalHash) 'Original profile journal changed.'
Assert ((Get-FileHash -LiteralPath (Join-Path $journal.backupPath 'personal.txt')).Hash -eq $personalHash) 'Original backup changed.'
# Crash after profile publication but before transaction completion: restore prior disposable state.
$transaction.state = 'Prepared'; $transaction | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $transactionPath
Reject { Invoke-Save Restore $bundle } 'interrupted shared restore'
Invoke-Save Recover $bundle
Assert ((Get-Content -LiteralPath (Join-Path $profile 'defaultES2File.txt') -Raw) -ceq 'new disposable world') 'Interrupted restore failed rollback.'
Assert (Test-Path -LiteralPath ($transaction.stage + '.failed')) 'Failed publication not retained.'
Invoke-Save Recover $bundle
Assert ((Get-Content -LiteralPath $transactionPath -Raw | ConvertFrom-Json).state -eq 'RolledBack') 'Recovery not idempotent.'
# Corruption and traversal-like manifest entries never select a destination path.
$bundleWorld = Join-Path $bundle 'profile\defaultES2File.txt'
[IO.File]::WriteAllText($bundleWorld, 'corrupt')
Reject { Invoke-Save Restore $bundle } 'hash verification'
Assert ((Get-Content -LiteralPath (Join-Path $profile 'defaultES2File.txt') -Raw) -ceq 'new disposable world') 'Corrupt bundle touched active profile.'
[IO.File]::WriteAllText($bundleWorld, 'host native world')
$manifestPath = Join-Path $bundle 'bundle.json'
$manifestText = Get-Content -LiteralPath $manifestPath -Raw
$manifest = $manifestText | ConvertFrom-Json; $manifest.files[0].relativePath = '..\personal.txt'; $manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $manifestPath
Reject { Invoke-Save Restore $bundle } 'hash verification'
[IO.File]::WriteAllText($manifestPath, $manifestText)
[IO.File]::WriteAllBytes($checkpoint, [byte[]]@(5,6,7))
Reject { Invoke-Save Capture (Join-Path $fixture 'stale') } 'receipt differs'
[IO.File]::WriteAllText((Join-Path $profile '.mscmp-disposable-profile'), ('0' * 32))
Reject { Invoke-Save Restore $bundle } 'marker'
[IO.File]::WriteAllText((Join-Path $profile '.mscmp-disposable-profile'), $journal.id)
& $prepare -Action Restore -ProfilePath $profile -JournalPath $journalPath -SteamCloudDisabled | Out-Null
Assert ((Get-Content -LiteralPath (Join-Path $profile 'personal.txt') -Raw) -ceq 'personal sentinel') 'Original profile recovery no longer works.'
Reject { Invoke-Save Restore $bundle } 'Prepared'
Write-Host "Passed $count shared-save bundle, corruption, interruption and personal-save sentinel assertions. Fixtures only."
