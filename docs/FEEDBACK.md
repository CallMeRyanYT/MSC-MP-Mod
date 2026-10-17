# Local error feedback

F7 captures a local problem report. The panel also provides a 240-character note and Report a problem button. This is useful for visual/behavioral problems that do not throw an exception.

The scene-owned observer subscribes to Unity's main-thread log event and unsubscribes during detach, scene destruction and quit. Error, exception and assertion events are captured only when the message or stack identifies MSCMP. Caught runtime, inspector, network and portrait failures are also recorded. Other mods' whole logs, save files, account files and screenshots are not collected. An assembly-load failure before this mod initializes cannot be captured by the mod itself; the loader console is needed for that case.

Reports are written below the loader's `GetModSettingsFolder(MSCMP)` result in `diagnostics/`:

- `feedback-latest.log`: current bounded report with mod DLL hash, scene/lifetime state, network counters, character selection, sanitized errors and recent mod events.
- `problem-latest.log`: last report explicitly requested by the player.
- `feedback-previous.log`: previous mod-instance report retained across menu/reload initialization.
- `feedback-status.json`: small run/revision/count record for the local monitor.

No upload endpoint is built into the DLL. The developer's own test environment has a separately configured Codex heartbeat that reads these files every five minutes during its prepared disposable-profile test. This monitor is not part of the download and does not read other players' PCs. It stays quiet when reports are absent/unchanged or the profile is restored. This monitoring depends on the Codex app's automation availability; the local files remain the durable evidence.

The buffer retains at most 32 entries and 64 KiB of UTF-8 report evidence. Identical sanitized errors are suppressed for ten seconds; suppressed/evicted counts remain visible. Messages and stacks are individually bounded. Incoming control/format characters are removed, malformed Unicode is normalized, and common Windows user-profile prefixes and IPv4 endpoints are redacted from errors, context and recent-event sections. This is targeted minimization, not a general secret detector; notes should describe game behavior.

Automatic disk writes are rate-limited to once per second, and report publication uses a staged file followed by replacement. Each complete report is limited to 256 KiB. A failed write retains in-memory evidence for retry, displays a local status and does not log recursively into its own error callback. Feedback does not change gameplay or personal-save paths.

Automated tests validate bounds, duplicate suppression, concurrent capture, sanitization and report publication/failure behavior. A local F7 report has been observed; automatic Unity-error callbacks, button flow and final quit reporting still require the real game checks in `PLAYER_CHECKS.md`.
