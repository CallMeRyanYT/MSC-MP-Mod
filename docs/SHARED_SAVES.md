# Passenger and shared saves (0.5.0)

The Hayosiko now has one driver and one front passenger. Stand within six metres, empty-handed, and choose the seat in F8 while paused. Boarding and exit require speed at most 3 m/s. The passenger cannot drive or pick up items. Exit checks for clear ground beside either front door; move the van to open ground if blocked. Disconnect/teardown restores walking and releases the seat. The passenger camera uses the inspected MassPassenger horizontal position and DriverHeadPivot height; remote bodies attach visually to their seats. Actual comfort, placement and cleanup require the final two-PC checks.

## Save and resume on the same host

- The host owns the shared save. Park and have everyone leave the van before saving.
- **Save shared items and van** captures the 22 supported items, Hayosiko transform and accepted host position. It does not save native money, needs, assembly or other progression.
- To retain native progress as well, use the game's normal toilet **Save & Quit while shared mode is still active**. The bridge captures shared state before the first native save action, ends network activity and restores native save controllers. The mod loader's subsequent save callback writes a receipt for offline bundling. A failed shared capture does not fabricate that receipt; check the feedback log.
- Next time, load the disposable game, keep **Resume saved shared items and van when hosting** checked, and Host. Friends Join normally and receive the restored supported-world baseline. They do not need the save bundle to rejoin.
- Restored items retain identity and orientation. Held items become free at their last accepted holder position. Bodies start stationary, all seats/hands are empty, and old peer credentials/requests are discarded.
- Files belong to `<disposable profile>/.mscmp-shared/`: `world.mscp`, `world.previous.mscp`, `session-generation.mscg` and `native-save-receipt.txt`. Atomic replacement retains the previous checkpoint. Corrupt-current recovery uses the valid previous file; corrupt/missing generation lease fails closed. No power-loss durability guarantee.
- Starting with Resume unchecked starts from the currently loaded native scene. It preserves the old checkpoint until the host explicitly saves again.

## Offline bundle, backup or host transfer

The bundle contains the **host's disposable native save files**, the multiplayer checkpoint and (when protected during profile preparation) typed game PlayerPrefs. It is private save data: do not commit it to GitHub or include it in a mod release. This preserves native progression for later loading; it does not make unsupported progression synchronized while playing.

Keep Steam Cloud OFF. Everyone exits the van, the host uses normal Save & Quit, then closes My Summer Car completely. The tool refuses a running game, missing receipt, wrong disposable marker or an unprepared/restored profile. It never writes the original personal-profile journal or backup.

Run from the mod package or repository. Set the existing profile preparation journal path and a new bundle directory:

```powershell
.\scripts\shared-save.ps1 -Action Capture -BundlePath 'D:\MSC-shared-saves\session-01' -JournalPath 'C:\path\to\journal.json' -SteamCloudDisabled
```

Copy that entire bundle folder to a friend only if transferring hosting. On the receiving PC, first prepare its own disposable profile using the included `test-profile.ps1`. Keep its original backup and recovery journal. With the game closed:

```powershell
.\scripts\shared-save.ps1 -Action Restore -BundlePath 'D:\MSC-shared-saves\session-01' -JournalPath 'C:\path\to\journal.json' -SteamCloudDisabled
```

Restore validates file hashes before moving anything. It preserves the previous disposable directory in a sibling `.mscmp-shared-before-<id>` directory and keeps this PC's disposable marker so its original personal-profile recovery still works. Then load the game and Host with Resume enabled. Everyone needs the same mod/game build.

If restore is interrupted, keep the game closed and run the same command with `-Action Recover`. This rolls back to the prior disposable profile/preferences and retains staged data. Do not delete the shared-restore journal or backup during recovery. If the original profile preparation skipped PlayerPrefs, pass `-SkipPlayerPrefs` to this tool as well; that bundle then contains files only and does not promise complete native preference/progress restoration.

The tool uses fixed game registry scope, bounded local files, no reparse points, exclusive profile-operation locking and same-volume directory moves. Fixture tests exercise corruption, interruption, invalid markers, journal preservation and eventual original-profile restoration. Real native save timing and two-PC host transfer remain manual checks in [PLAYER_CHECKS](PLAYER_CHECKS.md).
