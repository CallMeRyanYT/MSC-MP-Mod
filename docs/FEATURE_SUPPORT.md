# Feature support ledger

**Implemented code and headless tests are distinct from verified in-game support.** No milestone has been promoted on the strength of a compiler result alone.

| Feature | Implemented evidence | Runtime/support status |
| --- | --- | --- |
| Loader lifecycle, F8/F9/F10 diagnostics | Legacy build,26 lifecycle/trace assertions, bounded logs | Actual loader/scene/quit test pending |
| Host/Join/Leave, ping/probes | Pinned protocol2 transport; real localhost UDP, impairment, reconnect, bounds and repeated disconnect tests | Available as diagnostics; no two actual MSC processes or Tailscale evidence |
| Stable entity identity | Explicit authored mapping, host allocated IDs, ambiguity rejection and tombstones | Pure domain tested; no concrete game item resolver enabled |
| Remote walking characters | Inspected controller/camera; original male/female meshes, portraits, procedural walking, validated appearance baselines and bounded removal retries | Optional visual preview; rendering/placement unverified; no authoritative controller correction |
| Error feedback | Automatic mod-error capture, F7/manual note, bounded/redacted atomic local reports and quiet five-minute monitor | Real Unity callback/button/save-result check pending |
| Pickup/hold/drop/throw | Atomic host ownership/hand transactions, replay/epochs/races; synthetic world over UDP | No vanilla pickup interception or rigidbody adapter; unavailable gameplay |
| Late join/catch-up/reconnect | Immutable snapshots, bounded chunks/tail, Ready barrier and stale-generation rejection | Headless verified; vanilla bootstrap and dynamic factories unavailable |
| Mod checkpoint recovery | Versioned hashed snapshots, atomic backup, recovery-generation lease, offline normalization | Own data only; not a vanilla+mod coherent save |
| Disposable profile preparation | Whole-directory journal/recovery, typed PlayerPrefs backup, fixture regressions and real Prepare operation | Original ten-file backup hash-verified; Cloud off; disposable profile active; game-write/restore gate pending |
| Four participants | Host+3clients in local headless UDP tests; max3remote preview figures | No four-player game verification |
| Vehicle seats/controls/physics | Design only | Unavailable |
| Satsuma assembly/fasteners | Design only | Unavailable |
| Needs, death/respawn, money, shops/jobs | Design only | Unavailable |
| Time/weather/sleep/NPCs/traffic | Design only | Unavailable |
| Other gameplay mods | Conservative enabled-binary manifest equality | No gameplay compatibility claim |

The running diagnostic/preview build leaves ordinary single-player gameplay in place and never advertises it as a synchronized session. Enabling the pure authority world in game requires an explicit supported-item allowlist and pre-mutation blocking of unsupported shared-world actions.

Release evidence must name the game/loader fingerprints, exact supported item/vehicle/part family, build hash, actual runtime procedure/results, late-join and save fields, teardown behavior and remaining limits. See [TESTING](TESTING.md).
