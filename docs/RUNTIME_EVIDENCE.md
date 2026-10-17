# Runtime evidence

Recorded 2026-10-17 for v0.2.0, DLL SHA-256:
`A50171456FCC29D61B53F1F7B1916E9F2CA34EC425DE41D3FB3700507BE0E35B`

Target: Steam build 20171487, Unity 5.0.0f4, MSCLoader 1.4.2.410, 64-bit game.

## Observed from local mod diagnostics

- Menu initialization and a player-requested F7 report.
- Host listening on UDP 20577.
- Scene cleanup closing networking when leaving the menu.
- Intro/GAME observer transitions and pre-load, OnLoad and post-load callbacks.
- GAME loader-ready state.
- Read-only player and coffee-cup bindings resolved. Walking pose was unavailable at that observation; its cause is not established.
- A later join attempt failed with ConnectionFailed. No peer acceptance was observed.

These are bounded log observations from one game, not a witnessed two-player test or proof of visual correctness.

## Not established

- Successful two-PC or tailnet game connection and exchanged avatars.
- Correct male/female portrait rendering and remote animation.
- Full quit/reload behavior and runtime original-save restoration.
- Shared items, vehicles, assembly, world state or coordinated save persistence.

The last group is not integrated in this build. Headless authority/replication tests use synthetic entities. Keep these boundaries separate from the automated suite results and follow PLAYER_CHECKS.md for new evidence.

Repository-setup check: a new .NET Framework 4 headless run failed at rapid reconnect setup round 3. The recorded successful legacy suite predates this run. This intermittent transport liveness gate remains open; see KNOWN_ISSUES.md.
A diagnostic rerun passed all 4,459 headless assertions. The first failure remains an unresolved intermittent reconnect observation, not a confirmed fix.
