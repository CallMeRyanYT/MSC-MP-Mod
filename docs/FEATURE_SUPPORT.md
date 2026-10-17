# Feature support ledger

**Implemented code and headless tests are distinct from verified in-game support.** No milestone has been promoted on the strength of a compiler result alone.

## 0.3.0 experimental source and package

The table describes current source. It includes a disposable-profile-only shared session, authored cup/van mapping, host body simulation, full orientation/spin in world snapshots, custom cup pickup/drop/throw, guarded Hayosiko driver entry/exit, connection-bound controls, and automatic character startup. Native pickup/cup use and unsupported van FSMs are gated. All game adapters await in-game validation. [Exact van scope](VEHICLE_BINDING_EVIDENCE.md).

| Feature | Implemented evidence | Runtime/support status |
| --- | --- | --- |
| Loader lifecycle, F8/F9/F10 diagnostics | Legacy build,26 lifecycle/trace assertions, bounded logs | Actual loader/scene/quit test pending |
| Host/Join/Leave, ping/probes | Pinned protocol2 transport; real localhost UDP, impairment, reconnect, bounds and repeated disconnect tests | Available as diagnostics; no two actual MSC processes or Tailscale evidence |
| Stable entity identity | Explicit authored cup/van mapping, host allocated IDs, ambiguity rejection and tombstones | Domain tested; game mapping unverified |
| Remote characters | Inspected controller/camera; male/female meshes, portraits, walking/carrying/seated poses, appearance baselines and removal retries | Automatic in shared mode; rendering/placement unverified; no controller correction |
| Error feedback | Automatic mod-error capture, F7/manual note, bounded/redacted atomic local reports and quiet five-minute monitor | Real Unity callback/button/save-result check pending |
| Pickup/hold/drop/throw | Atomic host transactions, replay/epochs/races and a gated cup rigidbody adapter | Coffee cup only; runtime behavior unverified |
| Late join/catch-up/reconnect | Immutable snapshots, bounded chunks/tail, Ready barrier and stale-generation rejection | Headless verified; vanilla bootstrap and dynamic factories unavailable |
| Mod checkpoint recovery | Versioned hashed snapshots, atomic backup, recovery-generation lease, offline normalization | Own data only; not a vanilla+mod coherent save |
| Disposable profile preparation | Whole-directory journal/recovery, typed PlayerPrefs backup, fixture regressions and real Prepare operation | Original ten-file backup hash-verified; Cloud off; disposable profile active; game-write/restore gate pending |
| Four participants | Host+3clients in local headless UDP tests; max3remote preview figures | No four-player game verification |
| Vehicle seats/controls/physics | One-driver authority, control validation, stale-input braking, Hayosiko body/entry adapter and transform smoothing | Hayosiko body/driver only; runtime behavior unverified |
| Satsuma assembly/fasteners | Design only | Unavailable |
| Needs, death/respawn, money, shops/jobs | Design only | Unavailable |
| Time/weather/sleep/NPCs/traffic | Design only | Unavailable |
| Other gameplay mods | Conservative enabled-binary manifest equality | No gameplay compatibility claim |

Diagnostic mode leaves ordinary single-player gameplay in place. Shared mode replaces pickup and gates the mapped van's native FSMs. It does not make unbound game objects or progression authoritative; do not use it for a persistent cooperative playthrough.

Release evidence must name the game/loader fingerprints, exact supported item/vehicle/part family, build hash, actual runtime procedure/results, late-join and save fields, teardown behavior and remaining limits. See [TESTING](TESTING.md).
