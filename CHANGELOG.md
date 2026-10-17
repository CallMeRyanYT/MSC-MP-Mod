# Changelog

## 0.3.0 experimental - 2026-10-17

- Add an explicit disposable-profile-only game adapter for the persistent coffee cup and Hayosiko body, with baseline mapping before client interaction.
- Add host pickup/drop/throw integration, body rotation/angular velocity in world and checkpoint schema 2, and independent avatar/world/vehicle routing.
- Add guarded driver entry/exit, one-driver authority, request replay, bounded controls, stale-input braking, disconnect revocation and controller/scene cleanup. Actual game behavior remains unverified.
- Add 162 vehicle authority/codec assertions, body orientation coverage, carrying/seated poses and independent world/vehicle/avatar routing checks.
- Fix misaddressed UDP sends on legacy .NET by serializing socket send/receive operations. A three-client regression fails on the previous socket source and passes 100,800 payloads across sequence wrap with the fix. This resolves the reproduced bootstrap/commit stall mechanism; it is not a guarantee against all network failures.
- This is development source, not completion of all items, vehicles, assembly, progression or vanilla saves. Manual game tests are deferred at the user's request.

## 0.2.2 experimental - 2026-10-17

- Fix a transport cleanup race: remove the exact retired peer before emitting disconnect callbacks, protect incoming lookup/state changes with the collection lock, and ignore duplicate teardown for a replaced peer.
- Add a deterministic regression that reconnects inside a disconnect callback and proves old cleanup cannot remove the replacement. It fails on the previous implementation.
- Five consecutive real UDP network stress runs passed after the fix. Canonical validation now has 4,463 C# assertions, including 578 network assertions.
- Includes the v0.2.1 shipped-runtime compatibility fixes. Both players must update; real-game confirmation is pending.

## 0.2.1 experimental - 2026-10-17

Compatibility hotfix for the reported host disconnect during a friend's connection attempt.

- Replace unavailable System.IO.InvalidDataException with mscorlib FormatException throughout protocol, world-sync and checkpoint validation; rejection and error handling remain intact.
- Remove two additional unsupported APIs identified in the shipped game libraries: the console-color setter and FileStream's FileOptions constructor. Checkpoint writes use buffered flush and atomic replacement; physical power-loss durability is not established.
- Add a metadata-only audit against the actual game Managed libraries to the canonical test pipeline. The old DLL fails; the new DLL resolves 239 type and 748 member references.
- Pass 4,459 legacy C# assertions, 16 tooling scenarios and 19 profile-recovery assertions.

Both peers must update. Two-PC game confirmation is still required; the separately documented intermittent rapid-reconnect limitation remains open.

## 0.2.0 experimental - 2026-10-17

First published diagnostic and character-preview build.

- Private LAN/tailnet Host/Join/Leave, compatibility handshake, ping and reliable probes.
- Original male/female character meshes and portraits with optional walking-pose relay.
- Automatic bounded local mod-error reports, F7 capture and player notes.
- Read-only player/cup inspection and scene-owned cleanup.
- Pure authority, snapshot/catch-up and checkpoint-recovery foundations.
- Legacy build, guarded deployment/rollback, allowlisted packaging and reversible test-profile tooling.
- Recorded legacy validation: 4,459 C# assertions, 16 tooling scenarios and 19 profile-recovery assertions.

Single-game logs provide partial runtime evidence; two-PC connections and character rendering still need verification. Shared items, vehicles, assembly, economy and vanilla saves are not synchronized. See docs/RUNTIME_EVIDENCE.md and docs/INSTALL.md.
