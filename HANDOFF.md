# Current handoff

Updated 2026-10-17. Version0.2.0. Full requested scope is retained in `docs/PROJECT_BRIEF.md`.

## Delivered scope

- Legacy build/deploy/rollback/package pipeline and scene-owned diagnostics.
- Pinned LiteNetLib0.7.7.2 source with documented packet/queue/disconnect fixes; protocol2 handshake, identity, bounded application messages, ping, probes and poses.
- Host entity registry, single-hand item transactions, replay protection, epochs, tombstones, snapshot/catch-up/ready barrier and strict replica validation.
- Bounded world message framing connecting the authority engine to real UDP. Headless tests use a host plus three clients and synthetic items.
- Versioned hashed mod checkpoints, atomic backup/recovery and durable recovery-generation reservation.
- Inspected walking-player and coffee-cup contracts, original faceted male/female characters, procedural walking and selectable portraits. Appearance v2 baselines are required before poses; failed removal sends are retried within bounds.
- Local automatic error capture, F7/manual-note reports and a five-minute Codex feedback monitor. F6 checks read-only player/cup bindings.
- Reversible whole-profile test preparation/recovery with typed PlayerPrefs backup, crash journal and isolated fixture tests.

This is **not complete playable multiplayer**. No real item adapter, authoritative controller correction, vehicle/seat adapter, assembly/fastener integration, shared economy/world simulation, or coordinated vanilla+mod save restore is enabled.

## Evidence and boundaries

The installed target is Steam build20171487 / Unity5.0.0f4 / MSCLoader1.4.2.410. `COMPATIBILITY.md` records static evidence. `artifacts/current/build-manifest.json` records the exact built DLL and source/reference hashes; `.local/tests/results.json` records the corresponding successful test run. `.local/deployment.json` identifies the installed owned DLL and rollback copy. Never infer the installed hash from a previous build.

Automated suites cover lifecycle/trace, authoritative contention/replay, real localhost UDP and impairment, world bootstrap/races/disconnect, avatar relay, checkpoint corruption/recovery, deployment/package allowlisting and test-profile crash recovery. These do not establish Unity/Mono runtime integration.

Latest canonical run passed 4,459 C# assertions (26 lifecycle, 2,372 authority, 574 network, 502 sync, 245 avatars, 35 feedback buffer, 50 feedback file publication, 655 persistence), plus 16 deployment/package scenarios and 19 profile-recovery assertions. The feedback monitor is the local thread heartbeat `msc-mp-test-feedback`; it reads only this mod's reports, remains quiet for unchanged/absent reports, and does not deploy during play.

Manual isolated game testing has produced partial menu/GAME initialization, hosting, feedback and binding observations; see docs/RUNTIME_EVIDENCE.md. A join attempt failed, and two-PC/rendering/save-restoration gates remain open. The developer checkout has a separate local recovery journal; this is not supplied to other testers. Every tester must prepare and restore their own profile using docs/INSTALL.md. Keep Cloud off until original files and PlayerPrefs are restored and verified.

## Exact next gate

1. Prepare or verify the tester-specific disposable profile and backup against its journal; see `docs/INSTALL.md`. Do not replace an active recovery journal.
2. Follow `docs/PLAYER_CHECKS.md`: confirm initialization, both portraits, Host/Leave, F6 bindings, F7/notes feedback and F8/F9/F10, then return/re-enter GAME and quit. Inspect lifecycle/feedback logs and restore/verify the profile.
3. Repeat with two independently isolated game instances/PCs: matching diagnostics handshake, probes, ping, rejection, disconnect and original avatar preview.
4. Only then wire one inspected item family. The active pickup FSM performs local mutation before its final picked state; post-event observation is insufficient. Mesh damage saves on disable independently of the toilet-save FSM.
5. Pass actual pickup/throw/race/join/disconnect and save-restoration evidence before expanding vehicles, assembly or broader world systems.

Milestones0 and1 remain runtime-unverified. The later pure-logic work is implemented ahead of those gates, not proof that the gameplay milestones have passed. There is no measured four-player game or performance/soak result. The public download is an experimental diagnostics prerelease, not a completed gameplay release.
