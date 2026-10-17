# Known limitations and open gates

Version0.2.1 is an experimental diagnostic/visual-preview build. It is not a complete playable multiplayer release.

- **Partial single-game evidence only.** Menu/GAME loader callbacks, hosting and manual reporting were observed. The join attempt failed; two-PC exchange, Tailscale, avatar rendering and complete teardown remain unverified. See RUNTIME_EVIDENCE.md.
- **Personal-save isolation not proven through full runtime restoration.** Every tester must prepare a disposable profile, keep Steam Cloud off during testing, and restore/verify with the same journal afterward. The game has mesh-damage writers on disable, ordinary ES2 writers and PlayerPrefs, so a post-save hook alone is insufficient.
- **Vanilla items are not synchronized.** Authority and real-UDP world tests use synthetic entities. Exact pre-mutation pickup interception, stable binding, host physics, actor-aware FSM completion and movement correction remain required.
- **Avatar preview is visual-only.** Original male/female meshes and procedural walking are implemented; collisions, held anchors, seated/crouched/death animation and authoritative correction are unavailable. Host and clients must enable preview. Runtime appearance and placement remain unverified.
- **Small bounded world transfer.**128KiB blob limit and90-second join deadline; full game worlds/saves are not supported. Current pure world motion commits use the ordered event log; production physics needs an independent paced freshness stream.
- **Checkpoint scope is mod-only.** It does not produce a coherent vanilla-world checkpoint or restore game objects.
- **Conservative mod matching.** Every enabled mod binary is hashed. Even an unrelated cosmetic-mod difference rejects admission until a narrower compatibility policy is inspected.
- **No vehicles, Satsuma assembly, economy/jobs, needs/death, weather/time/NPC coverage.** These are open implementation and verification work.
- **No measured game performance, four-player game session or soak.** Local UDP tests do not establish these.
- **Private LAN/tailnet only.** No encrypted application channel, public-server identity authentication, host migration, lobby discovery or NAT traversal.

The upstream collection-removal exception observed in automated stress was patched in the pinned source and regression tested; see `vendor/LiteNetLib/PATCHES.md`. It is not evidence that the library has loaded inside this game's Mono runtime.

A duplicated entity/effect, dual holder/driver, ambiguous binding, partial readiness or damaged client save blocks release of the affected scope. Record actual build hashes and paired logs, and retain the original progress.

A fresh modern-host headless run during repository setup failed at rapid reconnect setup round 3 in NetworkTests.PeerRemovalRaces. This is a connection-establishment timeout, not the earlier collection-removal exception. Rapid endpoint reuse remains an unresolved transport liveness limitation; end/restart a stalled diagnostic session. A previous successful legacy test run does not erase this observation.

Version 0.2.0's receive-path TypeLoadException is addressed in v0.2.1 by replacing missing runtime APIs. Both peers must update. Static API audit and automated suites pass; actual game handshake confirmation is pending.
