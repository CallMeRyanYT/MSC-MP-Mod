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

## v0.2.1 compatibility hotfix

The v0.2.0 GAME host stopped during packet reception with TypeLoadException for System.IO.InvalidDataException. This is a mod/runtime incompatibility, independent of the earlier connection timeout. Version 0.2.1 removes that type reference and two further unsupported APIs found by a static audit. DLL SHA-256: 360184D3D47E9703081A4DF515211AF24EDD52731A793CB1A174117E6EC23C2D. The old DLL fails the audit; the new DLL resolves 239 type and 748 member references. The canonical 4,459 C# assertions, 16 tooling scenarios and 19 recovery assertions pass. No successful two-PC game connection with this hotfix has yet been observed.

## v0.2.2 transport cleanup regression

A deterministic reconnect-inside-disconnect-callback test fails before the cleanup fix and passes afterward, including duplicate teardown protection. Five consecutive UDP stress runs pass. The canonical suite passes 4,463 C# assertions, 16 tooling scenarios and 19 recovery assertions. Current DLL SHA-256: DC98DB55BAE14674DD596B381C9E7FCCCF2A7920E2F2E3CF8621DE446D4828BC. Two-PC game verification is still pending.

Qualification: the first v0.2.2 canonical attempt timed out during synthetic-world bootstrap. A rerun passed the full suite; repeated world stress then reproduced the bootstrap stall. These later observations supersede any blanket interpretation of the successful suite result. The installed diagnostic flow does not start world replication.
