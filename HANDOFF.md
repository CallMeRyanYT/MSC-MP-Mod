# Current handoff

Updated 2026-10-17. Version 0.5.0 experimental. Full requested scope remains in docs/PROJECT_BRIEF.md; it has not all been implemented.

## New in 0.5.0

The owner reported the preceding build working, without a scenario-by-scenario record. Added one Hayosiko passenger, host save/resume, native Save & Quit bridge and offline shared-save bundles. See docs/SHARED_SAVES.md and the new checks at the end of docs/PLAYER_CHECKS.md. These new game behaviors are not independently runtime-verified. Personal saves were not used for development/testing.

## Implemented scope

- Legacy build/deploy/rollback/package pipeline, scene-owned diagnostics, bounded local feedback and manual notes.
- Private LAN/tailnet host/client networking, matching-build admission, per-connection generations and reliable application routing.
- Host entity/hand authority, replay protection, authored identity, snapshots/catch-up/readiness and mod-only checkpoint schema 2 with body orientation/spin.
- Disposable-profile-only game adapter for the 22 persistent items: custom pickup, hold, drop and throw; host physics and client replicas. Native pickup/use and trigger colliders are gated during the session. See docs/SHARED_ITEM_CATALOGUE.md.
- Hayosiko body/driver adapter: guarded native entry/exit, one-driver authority, validated configured controls, held starter on I, stale-input braking, disconnect revocation, client body smoothing and scoped cleanup. Native van FSMs other than entry are gated. See docs/VEHICLE_BINDING_EVIDENCE.md.
- Original male/female characters with selectable portraits and walking/carrying/seated poses. Automatic startup in shared mode; optional preview in diagnostic mode.
- Native socket send/receive serialization fixes a reproduced legacy .NET cached-address race that delivered a packet to the wrong client. The old source fails the new regression; the fix passes 100,800 payloads across sequence wrap and a focused 144,000-payload run. Temporary wire traces and packet-pool instrumentation were removed.

- 0.5.0 adds bounded live commit batching, immutable action retries, result-watermark barriers, correlated walking corrections and host-approved avatar positions. One pose source avoids the prior duplicate shared/preview sends. A rejected peer no longer freezes the host game adapter.

## Evidence and boundaries

Installed target: Steam build 20171487 / Unity 5.0.0f4 / MSCLoader 1.4.2.410. COMPATIBILITY.md records static evidence. artifacts/current/build-manifest.json records exact source/reference/DLL hashes; .local/tests/results.json records corresponding automated results. .local/deployment.json identifies the installed owned DLL and rollback copy. Never infer current deployment from the source version alone.

The legacy suites cover authority, actual localhost UDP/impairment/fanout, world bootstrap/races/multiplexing, vehicle controls, characters, feedback and checkpoint recovery. Tooling/profile fixtures use generated test directories. These are not Unity gameplay tests. Reflection-created vehicle calls and native FSM ordering still require actual game observations.

Manual game tests are deferred until the user's end-of-build testing. Do not repeatedly ask for intermediate two-PC checks. The combined checklist is docs/PLAYER_CHECKS.md. Prior single-game evidence was limited to diagnostics; the old join attempt failed. The owner reported the preceding build working; no per-scenario evidence was provided. New passenger/save behavior and four-player soak remain unverified.

Personal saves and the original recovery backup must remain untouched. The local active disposable-profile journal belongs to this checkout and is not distributed. Other testers must prepare their own profile. Keep Cloud off until normal quit, restoration and verification. The five-minute feedback heartbeat remains read-only and does not deploy or control the game during play.

## Remaining work

Dynamic item families and item use, other vehicles, wheel/door/damage and fuel/electrical replication, Satsuma assembly/fasteners, shops/jobs/economy, needs/death, time/weather/NPCs and terrain-aware player validation remain unfinished. The new shared-save workflow preserves the host native files offline; unsupported native progression is not synchronized live. These gaps must remain visible in release notes rather than being counted as complete because the build passes.

Commit and push completed repository changes, as requested by the user. Do not publish private game binaries, asset dumps, logs, local configuration, profile journals or saves. Package only the allowlisted mod DLL, documentation, license/notices and profile tool.