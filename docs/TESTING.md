# Testing and acceptance evidence

## Current gate

Milestone 0 requires **real-game initialization and cleanup evidence**. Multiplayer remains unavailable. A compiler result, code review or pure bookkeeping test cannot establish that the loader invokes callbacks correctly in the installed game. No test below should be recorded as passed merely because its procedure exists.

Use `COMPATIBILITY.md` and the handoff for the actual build/automated-test results. This checklist starts as **awaiting manual test** for game integration. Record each executed step with its observation rather than replacing the checklist with a blanket “tested”.

## Local build and automated checks

From the repository root in PowerShell, configure `config/local.json` using `config/local.example.json` if discovery needs an override. `MSC_GAME_DIR` and `MSC_MODS_DIR` are supported alternatives; use the verified loader-selected Mods directory. Keep local configuration, game binaries, generated inspection output and logs out of Git.

```powershell
.\scripts\inspect.ps1
.\scripts\build.ps1
.\scripts\test.ps1
```

Preserve the command results and `artifacts/current/build-manifest.json` with the test record. The manifest identifies compiler, source and reference hashes. The runner checks lifecycle/trace, authority/replay, actual localhost UDP (including seeded impairment), synthetic-world replication with a host and three clients, avatar relay, mod checkpoint corruption/recovery, legacy assembly dependencies, and isolated deployment/profile-recovery fixtures. These tests do not launch Unity, establish loader callbacks or prove in-game save isolation/multiplayer. If a command fails, record it and stop dependent steps.

## Prepared disposable-profile recovery tool

`scripts/test-profile.ps1` is included in the experimental package. It defaults to the actual `AppData/LocalLow/Amistech/My Summer Car` profile and the exact `HKCU/Software/Amistech/My Summer Car` PlayerPrefs key. Every tester must prepare their own disposable profile before New Game; see INSTALL.md. Current development observations are recorded in RUNTIME_EVIDENCE.md. Prefer a separate Windows test account for additional PCs when available.

With every game process closed, first verify Steam Cloud is disabled for this game in Steam. Do not run Prepare again while a recovery journal is active. For a fresh test, use:

```powershell
.\scripts\test-profile.ps1 -Action Status
.\scripts\test-profile.ps1 -Action Prepare -SteamCloudDisabled
```

Prepare journals and hashes every original profile file, snapshots typed PlayerPrefs values, moves the entire original directory to a unique sibling backup and creates an empty marked test directory. It does not delete original progress. The flag is an explicit assertion by the operator, not automatic Cloud detection. Do not use `-SkipPlayerPrefs` outside fixtures. Keep Cloud disabled throughout testing and restoration.

After the game closes, including after a crash, recover with the same journal:

```powershell
.\scripts\test-profile.ps1 -Action Restore -SteamCloudDisabled
.\scripts\test-profile.ps1 -Action Status
```

Restore archives disposable progress separately, restores the original directory and typed PlayerPrefs, and verifies original file hashes. Interrupted phases can resume. Do not delete or edit the journal/backup to bypass a refusal. Re-enable the original Cloud setting only after successful restoration; review any Steam sync conflict using the restored original progress. This tool isolates the inspected directory/key for a controlled single-process test; it is not an implemented multiplayer-client save redirection mechanism and does not establish isolation of arbitrary other mods or external exporters.

## Milestone 0: one real game process

**Save prerequisite:** the current personal profile contains real progress. Do not click New Game, replace/delete a save, or launch a second process in that profile as a testing shortcut. Prepare a separate disposable Windows/game-save test profile whose entire game-save scope can be discarded. Verify that account's save location and loader setup before testing. A second game folder alone is insufficient. If this cannot be established, leave this gate awaiting manual test and collect only the static/build evidence above.

1. Close My Summer Car. Use the disposable profile's verified game/loader configuration and Mods directory for deployment. Review the target before installing:

   ```powershell
   .\scripts\deploy.ps1 -WhatIf
   .\scripts\deploy.ps1
   ```

   The installed artifact is `MSCMP.Mod.dll`, with mod ID `MSCMP`. Record its SHA-256 from the build manifest. Deployment must refuse a running game, changed references, or an unrecorded/modified existing DLL; do not bypass a refusal by deleting a file you have not identified.

2. Launch **one** game normally under the disposable profile. At the main menu, confirm the loader lists `MSC-MP diagnostics` without an assembly-load error. Open the loader console and look for `[MSCMP]` records including `mod.initialized`, `coverage`, `scene.begin` and `loader.menu`. Record game/loader versions and whether another mod is enabled. Absence of a callback/log is a failure to investigate, not a pass inferred from the DLL being present.

3. Load or create only disposable test progress. Wait for the gameplay scene to finish loading. Press **F8** to show/hide the diagnostic overlay (the key is rebindable in loader settings). Confirm it explicitly reports discovery mode/multiplayer unavailable. Record the displayed scene, `Loader OnLoad observed`, active generation, start count and cleanup count. Also preserve the diagnostic run ID from `mod.initialized` or the snapshot header: counters belong to one mod instance/run and may reset if the loader recreates it. The `loader.on-load` and `scene.loader-ready` records mean the callback was observed; they do not verify gameplay bindings or multiplayer readiness. A stable gameplay scene must have one active generation with one corresponding start; ordinary frames must not increase the start count.

4. Press **F9** while aiming at a nearby ordinary collider, without picking up or using the object. The read-only inspection is limited to a 6 m raycast, six ancestors, eight FSMs and sixteen state names per FSM, with an 80-row total inspection budget that can truncate earlier. Record whether it reports a hit, no hit or unavailable camera; all are observations, and a no-hit result alone is not a lifecycle failure. On a hit, verify output stays bounded and reports only inspection data. This does not establish a stable entity ID or a valid interaction hook. No item should move or change state because of the inspector.

5. Press **F10** to export a snapshot. The mod uses the loader API's settings folder for this mod, then `diagnostics/snapshot.log`; automatic lifecycle snapshots use `diagnostics/latest.log`. A new mod instance rotates the prior latest snapshot to `diagnostics/previous.log` when that existing file is within the 256 KiB rotation bound. Resolve the base through the loader's actual configured mod settings location rather than assuming a universal absolute path. Confirm the snapshot exists and contains the matching generation plus `snapshot.requested`; the file trace uses event names without the console's `[MSCMP]` prefix. Snapshots contain separate sections for 64 retained lifecycle records and 128 recent events. The same timestamp/event in both sections is **one occurrence**, not a duplicate callback. Repeated inspection/focus/export events do not evict the separate lifecycle history. If writing is unavailable, preserve the loader console/output log and the `diagnostic.file-error` result instead.

6. Pause/unpause, switch focus away and return, then toggle the overlay again. Record whether input, diagnostics and game behavior recover without duplicated scene starts or exceptions. No networking liveness claim follows from this single-process check.

7. Return through the game's normal menu/scene-exit flow using only disposable progress. Check `scene.end` for the old generation and verify its cleanup count increases **once**. Preserve the loader console/output log and `latest.log`/`previous.log` across this transition: only the most recent prior instance snapshot is retained. The menu may start its own observer generation, so compare individual `scene.begin`/`scene.end` records within each run instead of assuming the menu has no observer. Load that disposable gameplay scene again. Within the same run the new generation must differ; if the loader recreates the mod, a new run ID distinguishes reset counters. Each active generation has one start. If the loader replays `loader.on-load`, its loader-ready flag becomes true. If initialization callbacks are not replayed in the loader's Game scene, the loader update callback restores a missing observer and records `scene.observer-recovered`; its loader-ready flag stays false and inspection stays unavailable. This recovery does not run in menu/intro/ending scenes, where initialization remains callback-driven. Record which path occurs instead of treating recovered diagnostics as verified gameplay readiness. Repeat the exit/re-entry cycle once more. A delayed destroy callback from an old generation must not clear the new generation or count another cleanup for it.

8. If the installed loader exposes runtime disable/enable for this mod, disable then re-enable it once in the disposable scene. Record `loader.disabled`, one cleanup, `loader.enabled`, and one new active generation with the UI restored. Re-enable intentionally waits for the next `loader.on-load` before target inspection becomes available; `Loader OnLoad observed: False` until then is expected. Re-enter the disposable gameplay scene to exercise that path. If the control is unavailable, mark this subtest **not exercised** and retain the scene-transition results. Do not assume editing the DLL while the game runs tests this behavior.

9. Export one more snapshot with **F10**, then quit normally. Preserve `latest.log` immediately after exit: a subsequent launch can overwrite it. Check for one final cleanup of the remaining active generation, with no repeated cleanup, unhandled exception or leftover UI. Compare starts and cleanups within each diagnostic run; after all of its started scenes have ended, they should match. Include the actual records if callback order differs from expectations.

10. With the game closed, exercise removal or a recorded rollback:

    ```powershell
    .\scripts\uninstall.ps1 -WhatIf
    .\scripts\uninstall.ps1
    ```

    Use `-RestorePrevious` instead only when this checkout recorded a previous diagnostic build. Confirm only the recorded mod DLL was removed/restored. Settings/diagnostics can remain. Relaunch the disposable profile if needed to confirm the mod no longer appears after removal. Do not use the personal save to validate an unproved multiplayer isolation mechanism.

**Pass evidence:** the build manifest, loader startup result, visible discovery overlay, bounded inspection/export result, complete scene re-entry trace, final cleanup trace and deployment/removal result all refer to the same build. Unexpected callback order must be reviewed before marking the gate complete. An unavailable interactive session leaves it **awaiting manual test**.

**Minimal report to return:** `latest.log`, `previous.log` if present, `snapshot.log`, build manifest, loader/game output log if an error occurred, plus the exact step that failed and what was visible. Keep run IDs and section labels intact so retained/recent copies of the same event are not double counted. Review full game logs before sharing: they may contain local paths or unrelated mods' output. Do not include save files, account tokens or an entire user directory.

## Future host/client tests — not runnable in milestone 0

Version 0.2.0 exposes Host diagnostics / Join diagnostics / Leave on UDP port 20577 by default, plus Ping and Reliable probe. In a ready GAME scene, the optional walking-avatar checkbox is available after connection acceptance; enable preview on host and clients. These controls do not create a shared item/save world. Use two separate PCs and independently disposable profiles initially. Multiple game processes on one account remain gated on save/config isolation. Do not advertise game support based on standalone console tests.

| Gate | Host steps | Joining tester steps | Required evidence |
| --- | --- | --- | --- |
| 1: transport | Start a private test host at the documented endpoint/UDP port; record fingerprint and session ID | Join from second actual MSC process; exchange bounded ping/pong and reliable messages; leave | Both game processes load the pinned dependency; matching handshake, reliable receipt, disconnect cleanup; mismatch rejected before mutation |
| 2: identity/avatar | Bind two inspected identical-looking authored items; walk/turn and move/reparent one | Verify both mappings independently, observe host, walk/turn; deliberately test pose correction | Distinct persistent mappings despite discovery order; one avatar per peer; corrected owner controller agrees with host interaction position |
| 3: shared item | Race pickup of the one allowed item with client; drop, throw and rapidly pick up again | Request same item concurrently; observe holder, retrieve after confirmed drop; retry a duplicated request | Exactly one entity and holder; matching hand/item state; no stale reattachment; duplicate/ack-loss cases reconcile once |
| 4: join/catch-up | Hold an item while client joins slowly; release/spawn/consume supported items during transfer | Join/rejoin, remain unable to interact until Ready; verify final catalogue, tombstones and holder state | Snapshot watermark plus ordered tail; no partial readiness or lost/duplicated commit; overflow/restart fails or recovers cleanly |
| 4: disconnect | Observe held item when client disconnects; stop host in a separate trial | Disconnect while holding, reconnect with valid identity, then test host loss | One cleanup/release; no duplicate avatar, locked item, old-generation effect or automatic host promotion |
| 4: save isolation/recovery | Save during pickup/release, interrupt staged save before commit, reload | Compare all previously recorded original-save hashes/paths after join, ordinary save, scene exit, quit and crash recovery | Coherent before-or-after host state; recover previous complete generation; original client save unchanged across every inspected write path |
| 5: four players | Repeat earlier tests with four actual players | Repeat competing pickup, join/leave/reconnect and save checks | Four-player evidence and an original/licensed reproducible character asset build |
| 6: one vehicle | Compete for driver seat; drive; release/regrant seat; stop receiving controls | Race seat claim; re-enter with deliberately delayed old controls; late join | One driver; old control epoch rejected; defined input timeout; consistent host physics and save/reload |
| 7: assembly | Install inspected part while another player requests pickup/removal; duplicate a fastener request | Observe graph while joining; remove only when permitted; save/reload | One valid part/slot/holder graph; one fastener effect; required component/FSM state and persistent identity match |

For features not yet supported, attempting a world-changing action in a future multiplayer session must be visibly gated and leave shared state unchanged. Expand the ledger only after the relevant integration evidence and test results exist.

## Impairment, soak and performance records

After baseline two-PC LAN tests pass, repeat supported cases under a reproducible impairment configuration: approximately 100 ms RTT with jitter/1% loss, approximately 250 ms RTT with jitter/5% loss, explicit duplication/reordering, and a brief total interruption. Record the seed, exact loss/jitter policy, whether delay is one-way or round-trip, and measured RTT. Do not count a 100 ms one-way delay as a 100 ms RTT profile. Include one real Tailscale session and record direct/relay path where observable.

Run a measured 30-minute two-player session for the supported slice, including pickup/drop, race, join/leave and save/reload; repeat applicable coverage with four players at its gate. Add driving/assembly only after supported. Record actual duration and operation counts. At comparable semantic watermarks, check entity membership, lifecycle, revisions, holder/hand/slot relationships and quantities. Do not use interpolated render-position equality as the state consistency test.

Compare CPU/frame time, allocations, memory growth, bandwidth and snapshot size against the same scene without the mod. Record sampling method, hardware and game/mod fingerprints. Budgets must follow measurements; no performance claims are established by this plan.

## Evidence record template

```text
Date/time and tester:
Milestone / exact scenario:
Status: PASS | FAIL | NOT EXERCISED | AWAITING MANUAL TEST
Game/loader fingerprint and mod build SHA-256:
PCs, disposable profile/isolation evidence, relevant mods:
Transport version, endpoint method, observed Tailscale path:
Impairment settings/seed, one-way vs RTT, measured RTT:
Start/end time, duration and operation counts:
Steps actually executed:
Observed expected and unexpected results:
Session/generation/peer/request/commit IDs where applicable:
Before/after state or save hashes where applicable:
Log/screenshot/manifest locations (local/private unless reviewed):
Open defects and next exact test:
```

A duplicate entity/effect, dual holder/driver, damaged client save, permanent stale attachment, silently missing transaction or partial Ready state blocks release of the affected feature. Preserve evidence and repair the underlying integration rather than masking it with repeated resets.
