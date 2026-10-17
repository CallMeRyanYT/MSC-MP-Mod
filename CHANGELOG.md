# Changelog

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
