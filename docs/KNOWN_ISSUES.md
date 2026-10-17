# Known limitations and open gates

Version 0.4.0 contains unverified adapters for 22 persistent items and the Hayosiko. It is an experimental prerelease, not complete playable multiplayer.

The 0.4.0 adapter requires the active disposable-profile marker before changing game objects. The 22-item catalogue and van body/driver are mapped. Van doors/dashboard/native use and item use and trigger actions are gated; other vehicles, dynamic items, assembly and world progression remain unsupported. The vehicle panel has a nearby seat request and the starter key is I. Native hook execution, driving and exit cleanup still require actual game validation. The older released-build limits below remain applicable except where this paragraph explicitly adds implemented source.

- **Partial single-game evidence only.** Menu/GAME loader callbacks, hosting and manual reporting were observed. The join attempt failed; two-PC exchange, Tailscale, avatar rendering and complete teardown remain unverified. See RUNTIME_EVIDENCE.md.
- **Personal-save isolation not proven through full runtime restoration.** Every tester must prepare a disposable profile, keep Steam Cloud off during testing, and restore/verify with the same journal afterward. The game has mesh-damage writers on disable, ordinary ES2 writers and PlayerPrefs, so a post-save hook alone is insufficient.
- **Item support is pickup/body synchronization for the 22-item catalogue.** Native pickup is disabled in shared mode. Special use, burning, refuelling, cooking, tool operation and dynamic consumable factories remain unsupported. Closed/free/idle startup contracts are required; a missing or busy catalogue item refuses shared startup. Automated world tests use synthetic bodies, not Unity physics.
- **Characters remain presentation only.** Original male/female meshes, walking/carrying/seated poses and a hand anchor are implemented; collisions and crouching/death are unavailable. Correlated walking correction is implemented but needs game validation. Shared mode starts characters automatically. Runtime appearance and placement remain unverified.
- **Small bounded world transfer.**128KiB blob limit and90-second join deadline; full game worlds/saves are not supported. Current pure world motion commits use the ordered event log; production physics needs an independent paced freshness stream.
- **Checkpoint scope is mod-only.** It does not produce a coherent vanilla-world checkpoint or restore game objects.
- **Conservative mod matching.** Every enabled mod binary is hashed. Even an unrelated cosmetic-mod difference rejects admission until a narrower compatibility policy is inspected.
- **Vehicle coverage is one Hayosiko driver and the body transform.** Passengers, wheel/suspension/door/damage state, other vehicles, Satsuma assembly, economy/jobs, needs/death and weather/time/NPCs remain unfinished. Host pause stops host physics; independent world systems are not shared.
- **No measured game performance, four-player game session or soak.** Local UDP tests do not establish these.
- **Private LAN/tailnet only.** No encrypted application channel, public-server identity authentication, host migration, lobby discovery or NAT traversal.

The upstream collection-removal exception observed in automated stress was patched in the pinned source and regression tested; see `vendor/LiteNetLib/PATCHES.md`. It is not evidence that the library has loaded inside this game's Mono runtime.

A duplicated entity/effect, dual holder/driver, ambiguous binding, partial readiness or damaged client save blocks release of the affected scope. Record actual build hashes and paired logs, and retain the original progress.

A fresh modern-host headless run during repository setup failed at rapid reconnect setup round 3 in NetworkTests.PeerRemovalRaces. This is a connection-establishment timeout, not the earlier collection-removal exception. Rapid endpoint reuse remains an unresolved transport liveness limitation; end/restart a stalled diagnostic session. A previous successful legacy test run does not erase this observation.

Version 0.2.0's receive-path TypeLoadException is addressed in v0.2.1 by replacing missing runtime APIs. Both peers must update. Static API audit and automated suites pass; actual game handshake confirmation is pending.

The endpoint-cleanup race behind the observed rapid-reconnect setup failure is corrected in v0.2.2. The prior implementation fails the new deterministic callback-reconnect regression; the fix passes it and five consecutive UDP stress runs. This is not a guarantee against every endpoint-reuse or delayed-packet liveness problem.

The reproduced bootstrap/commit stall was traced to a legacy .NET socket-address cache race: a packet intended for one client arrived at another and advanced that client's reliable sequence. Version 0.3.0 serializes socket send/receive calls. The previous source fails the three-client fanout regression; the fix passes 100,800 payloads across sequence wrap. Broader endpoint-reuse and real-game liveness guarantees remain unproven.

Version 0.4.0 adds batched live commits and bounded immutable action retries. Motion remains on the reliable world stream; there is no independent large-world snapshot channel or measured performance guarantee. Full-game multiplayer remains unfinished.
