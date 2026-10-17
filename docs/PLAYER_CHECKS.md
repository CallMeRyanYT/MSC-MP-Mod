# End-of-build manual checklist

Run these checks when you are ready to test. Version 0.5.0 implements 22 shared pickup items and van driver plus one passenger and host save/resume; the full-game scope remains unfinished. Automated checks do not count as these game tests.

Use an independently prepared disposable profile on each PC, following [INSTALL.md](INSTALL.md). Keep Steam Cloud off. Retain the original preparation folder and journal for restoration; an already active profile must not be prepared again from a new ZIP. Do not copy personal saves into it.

1. Install the same 0.5.0 ZIP on both PCs while the game is closed. Load the disposable game and wait until you can walk. Put down any native held item. Open F8 using the pause menu.
2. Choose Male on one PC and Female on the other. Both portraits should be complete, textured and free of pink/black/flickering parts. Swap choices later and check that the old character disappears.
3. Enable **Shared world** on both PCs. Host on port **20577**; the friend joins using the host's LAN or authorized Tailscale IPv4 address. Wait for **Shared items and van ready (22 items)** on both. Record a refusal with F7; do not bypass a failed binding or profile check.
4. Hide F8 and unpause both games. Verify that each player sees the other's walking, turning and selected character. Try Ping from the panel. A listening host with zero peers is still waiting for a friend.
5. At the coffee cup, left-click within reach to pick it up. Both players should see exactly one cup held by the same player, with a carrying pose. Race to pick it up: only one may succeed. Left-click to drop; right-click while holding to throw. Check position and rotation on both PCs. Repeat pickup/drop/throw with the [22-item catalogue](SHARED_ITEM_CATALOGUE.md). Verify both axes and both bucket/dipper pairs remain distinct. Item use, dynamic consumables and unlisted objects are outside this build.
6. Stand within six metres of the Hayosiko, empty-handed. Use **Request van driver seat**. Only one player may receive the seat, including simultaneous attempts. Close F8, unpause, hold **I** to crank and use configured steering/throttle/brake/clutch/gears. The other PC should see the same body motion and the driver's seated pose. Then have the second player request the passenger seat. Both should remain seated while driving; only the driver can control the van. Stop before the passenger exits. Doors/dashboard, wheel/damage state and other vehicles remain outside this build.
7. Stop before exiting. Try the normal driving-mode key or **Leave van driver seat**. Confirm the walking camera/controller returns. Swap drivers. Open the panel while driving: controls should brake instead of continuing throttle. Host pause stops host physics.
8. Leave and rejoin while another player holds any mapped item or occupies the van. A joining player must wait for the baseline, then agree on the holder, driver and body position. Disconnect the holder/driver: the held item should release, the seat should clear and stale vehicle controls should stop within 500 ms while host simulation is running.
9. After a delayed action, wait for the correlated result; repeated clicks must not duplicate or lose an item. Walk again after a position correction and check that a nearby pickup succeeds. Repeat Host/Leave, then return to the menu and load again. Check for duplicate characters/UI, stuck hands/cameras, enabled client physics or continued vehicle movement after disconnect. Press F7 or add a short panel note for any failure; reports include this mod's recent diagnostics. They stay local until shared deliberately.
10. Quit normally. Record pass/fail observations and the installed version on both PCs. From each original preparation folder, restore and verify the personal profile using INSTALL.md before re-enabling Steam Cloud.

Useful keys: F6 read-only player/cup bindings, F7 problem report, F8 panel, F9 aimed-object inspection, F10 diagnostic export. Keys can be changed in the loader settings.

Do not judge unrelated assembly, shops/jobs, needs/death, weather/time, NPCs or vanilla save progression as synchronized. Those adapters remain unfinished. Use the current checklist to identify concrete failures in the implemented scope.
## Completion record

Do not mark a game check passed from an automated test. For each numbered check, record **Pass / Fail / Not tested**, both DLL hashes, host/client roles, and a short observation. Press F7 immediately after a failure and include the relevant local report when sharing it.

| Work | Status in 0.5.0 | Evidence still required |
| --- | --- | --- |
| Build and legacy API compatibility | Automated checks | Actual Mono execution |
| Host + three client protocol, retries, batches and correction logic | Automated tests | Two-PC connectivity and gameplay |
| Exact 22-item catalogue | Static bindings and implemented adapter | Pickup, physics and teardown for each item |
| Hayosiko driver/passenger/body | Implemented adapters and authority tests | Both seats, travel, exit and disconnect |
| Shared saves | Host resume and offline bundle recovery fixtures | Native Save & Quit, reload and host transfer |
| Male/female characters | Implemented original meshes and presentation | In-game appearance, hands and seated poses |
| Error feedback | Automated reporter tests; earlier F7 evidence | Reports from this build on each PC |
| Personal profile | Original backup untouched by this development work | Normal quit and restore/verification using original journal |
| Remaining game systems | Not implemented | Item use/factories, other vehicles, assembly, shops/jobs, needs/death and live weather/NPC/progression sync |

## New passenger and save checks

1. Driver and passenger enter from nearby with empty hands. A third player cannot take either occupied seat. Passenger throttle/steering/item clicks must have no authority. Test male and female seated appearance.
2. Drive, turn and brake. Passenger camera and remote seated bodies should follow without walking, jitter or leaving the van. Boarding/exiting above 3 m/s must be refused. Stop on open ground and exit, then walk normally.
3. Disconnect the driver: the van brakes and the passenger remains. Disconnect the passenger: their seat becomes available. Leave/rehost and reload the scene; no controller or seat lock should remain.
4. Park and have everyone exit. Move a supported item. Host clicks **Save shared items and van**, leaves, then hosts with **Resume** enabled. Check item and van positions, empty seats/hands, stopped bodies and friend late join.
5. Host uses the normal toilet **Save & Quit** while shared mode is active. Close the game. Reload and host with Resume enabled. Check the supported world and the host's native progress. Use F7 if a save error appears.
6. Follow [SHARED_SAVES](SHARED_SAVES.md) to capture and restore an offline bundle inside a prepared disposable profile. Check positions/native progress, then join. Keep the original personal-save backup intact throughout.

Report the first failed step with F7 and a short note. A previous build working does not establish these new checks passed.
