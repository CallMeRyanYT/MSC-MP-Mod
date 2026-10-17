# Networking diagnostic implementation

## Evidence and limits

The network module builds with the installed C# 3/.NET Framework 3.5 compiler, with warnings treated as errors. The automated suite exercises real localhost UDP sockets, handshake, bidirectional application ping/pong, 32 ordered reliable probes, typed application/pose delivery, disconnect, reconnect identity, and a useful content-fingerprint rejection. It also verifies malformed inputs, bounds, UTF-8 validation, finite pose values, stale application generations, duplicate messages, queue overflow, send-rate limits, unreachable endpoints, and handshake timeouts. A seeded localhost UDP relay tests 64 ordered probes with 35 ms one-way delay, +/-10 ms jitter, 5% loss and 5% duplication; jitter/due-time dispatch also reorders datagrams.

These are standalone .NET tests. **Loading and exchanging messages between two actual My Summer Car processes remains unverified. Milestone 1 is not complete.** Neither a compatible packet nor an accepted diagnostic peer grants world/gameplay readiness. The transport never changes Unity objects or saves; an application adapter is separately responsible for safe, verified game integration. Tests with four game processes, real Tailscale, cross-machine impairment and a 30-minute gameplay session remain pending. Root test output is the source of the current assertion count.

## Dependency decision

Selected [LiteNetLib 0.7.7.2](https://github.com/RevenantX/LiteNetLib/tree/5ba2470394092ac5924b1ea45da588cfa5a1e948) at exact commit `5ba2470394092ac5924b1ea45da588cfa5a1e948`, with MIT source and license vendored. The [upstream project](https://github.com/RevenantX/LiteNetLib/blob/5ba2470394092ac5924b1ea45da588cfa5a1e948/LiteNetLib/LiteNetLib.csproj) targets .NET 3.5. The transport portion compiles under C# 3 after omitting its unused optional reflection serializer. No binary downloaded from a third party is loaded. Only standard `mscorlib`, `System`, and `System.Core` are referenced by the standalone module.

[Current LiteNetLib 2](https://github.com/RevenantX/LiteNetLib) requires a newer runtime/Unity version. The inspected `v0.9.5.2` commit `eac1e4a242a04121c5d051ff53a2142e85429c63` targets .NET 3.5 but selects C# 4 and uses optional/named arguments in the core. That version could be compiled by a separately validated newer compiler; it was not silently assumed compatible. The selected older profile preserves the repository's actually installed C# 3 toolchain. See `vendor/LiteNetLib/PATCHES.md` for the small ingress/MTU restrictions and limitations. This is an established reliable UDP implementation, not a new retransmission algorithm.

## Integration API

`MSCMP.Network.DiagnosticSession(CompatibilityInfo, displayName)` exposes `Host(port)`, `Join(numericIPv4, port)`, `Poll()`, `SendPing()`, `SendProbe(text)`, `SendPose(x,y,z,yaw)`, `Leave()` and `Dispose()`. Host port `0` selects an ephemeral port for tests; the UI defaults to its configured UDP port. A host has at most three remote peers. `Event` is invoked only from caller operations/`Poll`; call these from Unity's main thread. All library workers only copy bounded bytes into the transport queue. No game references exist in this module.

`Status`, `PeerCount`, `AcceptedPeerCount`, `QueueDepth`, `LastPingMilliseconds`, `SentBytes`, `ReceivedBytes`, `ProbesReceived`, and `PongsReceived` support diagnostics. Byte counters count application payloads, **not** UDP/IP headers, retransmissions or measured loss. An application ping measures elapsed monotonic round-trip time including polling/scheduling delays. The transport uses its own internal keepalive; the diagnostic application timeout measures elapsed wall time rather than scaled Unity time.

All transport, protocol and session methods must be called by one owning application thread. The wrapper's queue synchronization exists for the library workers; it does not make arbitrary concurrent `Host/Join/Stop/Send` calls supported.

Protocol 2 adds `ApplicationReceived` (`ApplicationMessage` containing connection-bound `PlayerId`, `ConnectionGeneration`, `Sequence`, and `Payload`) and `PoseReceived` (`PoseMessage` with the same binding plus validated `X/Y/Z/Yaw`). `GetAcceptedPeers()` returns copied player/generation records. `SendApplication(playerId, bytes)` targets an accepted peer; clients can target only host player `1`. `BroadcastApplication(bytes)` is host-only. Application payloads are 1-384 bytes, reliable ordered, with a distinct per-peer application sequence. Both application and pose callbacks run on the owning thread. A body may contain arbitrary bytes; application parsers still must validate every operation/entity/claimed identity and use the callback's bound identity as the sender.

`SendApplication` returns false if its target is missing or the send budget is full; callers may retry without an application sequence gap. Broadcast returns true only if at least one target exists and all accepted targets queued successfully; partial delivery is possible. Use targeted per-peer queues for a chunked transfer and replay-safe application IDs if broadcasting retries. The transport has no snapshot chunk/ACK/cancellation implementation. `DisconnectPlayer(playerId, controlledReason)` rejects that bound peer on a host, or ends a client's host connection; pass a fixed bounded user-facing reason (1-96 UTF-8 bytes), never a raw exception/private data string.

## Admission and identity

The client sends a bounded display name, random client nonce, mod version, game fingerprint, content/dependency fingerprint, capability bits and optional host-issued reconnect token. Host compares the manifest exactly and returns a useful rejection before accepting the application connection. Hash creation and proving which fingerprint is supported belong to the mod's inspected compatibility layer; this module compares supplied values and does not claim to audit arbitrary peers' installations.

Host allocates the session ID, nonzero world/session generation, player ID and nonzero monotonically increasing connection generation. Display names never determine player identity. Accepted transport connection objects select the player record; messages cannot choose a sender ID. Client verifies its nonce and its own expected compatibility manifest in the host welcome. The host's own diagnostic identity is player `1`; remote players begin at `2`.

Reconnect tokens and random identities come from `RNGCryptoServiceProvider`, not an invented cryptosystem. The client retains its token in memory for the same endpoint. Host stores at most 16 token records, expires disconnected records after 120 seconds, rejects use by a currently active connection, and rotates the token on successful resume. Restarting the host loses tokens; create a fresh diagnostic client to join that new session. Tokens never appear in diagnostic events. This is plaintext authorized private co-op networking: tokens are replay credentials visible to a network observer, not encryption or authenticated public hosting.

On every established message, session ID, session generation and assigned connection generation must match. Old values are discarded without applying the message. Reliable probe sequences add application duplicate suppression and exact ordered progression; sequence comparisons use signed modular subtraction with the half-range ambiguity rejected. Player/connection counters cannot wrap into reused identities. Sequence freshness for the diagnostic pose is distinct from any future semantic entity revision.

## Binary wire schema

All integers and IEEE 754 single-precision floats use little-endian encoding. GUIDs use the .NET `Guid.ToByteArray()` byte layout. Strings use strict UTF-8 with an unsigned 16-bit **byte** length prefix, no terminator; control characters and malformed UTF-8 are rejected. There is no generic deserializer or peer-supplied CLR type.

Every message begins with the fixed 44-byte header: `uint32 magic=0x504D534D`, `uint16 protocol=2`, `uint8 kind`, `uint8 flags=0`, `16 bytes sessionId`, `uint64 sessionGeneration`, `uint64 connectionGeneration`, `uint32 sequence`. Unknown kinds/flags/versions, truncated fields, trailing bytes and messages over 480 bytes are rejected. The underlying LiteNetLib connection key is `MSCMP-DIAGNOSTICS-2`; it is a protocol routing key, not a password.

| Kind | Payload after header | Delivery |
| --- | --- | --- |
| 1 Hello | mod string <=32 bytes; game hash <=64; content manifest <=64; uint32 capabilities; name <=32; 16-byte client nonce; 16-byte reconnect token | Reliable ordered; zero session header |
| 2 Welcome | same compatibility fields; echoed nonce; uint64 assigned player ID; 16-byte new token | Reliable ordered; host-issued header |
| 3 Reject | reason string <=128 bytes | Reliable ordered; grace of 250 ms before local disconnect |
| 4 Ping / 5 Pong | int64 caller monotonic milliseconds; correlated uint32 header sequence | Reliable ordered |
| 6 Probe | text string <=256 bytes | Reliable ordered; duplicate suppression and gap detection |
| 7 Pose | float32 x, y, z, yaw | Sequenced; diagnostic only, no avatar binding |
| 8 Application | uint16 byte length, 1-384 opaque application bytes | Reliable ordered; independent application sequence and duplicate suppression |

Pose fields must be finite; absolute coordinate limits are 100,000 and absolute yaw is 360. These are parser limits, **not** validated game movement/range rules. Entity commands/snapshots and their authority rules belong to separately versioned application schemas; an opaque payload alone does not validate their semantics.

## Resource and lifecycle bounds

- At most three remote transport connections; at most 256 queued incoming events and 480 application bytes per data event.
- Raw UDP ingress is checked before packet allocation: at most 512 bytes, no fragments/merged packets/NAT/discovery/MTU negotiation, control packet minima/exact sizes and sequence range checked. Fixed upstream MTU is 508 bytes.
- At most 128 outgoing application messages per second per peer and 384 across the transport, at most 128 queued reliable messages per peer, plus upstream's fixed 64-packet in-flight reliable window. A full send budget returns `false`, leaving the application sequence unchanged so retry does not create a gap.
- The sequenced outgoing queue retains at most one not-yet-sent packet, replacing obsolete poses. It never substitutes for reliable transactional delivery.
- At most 256 application messages per peer per second. Queue overflow stops the whole diagnostic session with a visible error; it never silently drops accepted reliable application events.
- Five transport connection attempts spaced 500 ms apart, 5-second transport inactivity timeout, 5-second application handshake timeout. A rejected peer is disconnected after a bounded 250-ms grace. These values do not guarantee delivery of a final rejection under total packet loss.
- `Leave/Dispose` stops library sockets/threads and clears peer/event queues, then emits `Stopped` when an active local session was left so world adapters can release their state. Callback processing cannot resurrect queued peers after stop. Reconnect creates a new connection generation; a host restart creates a new session identity. A repeated 24-round concurrent local/remote disconnect regression covers the observed upstream peer-removal race.

There is no independent bulk channel in this old transport profile. Do not add a large baseline to this stream and call the snapshot gate complete. The next transport decision must assess a more recent legacy-compatible transport before adding bounded bulk chunks and public-facing capability.

## Actual game test still required

Use two safely isolated disposable Windows test profiles/PCs; save isolation must be established before launching two game processes against the same user's data. Install the same compiled diagnostic package on both. In the diagnostic overlay, host one UDP endpoint and join its numeric LAN/Tailscale IPv4 from the second process. Confirm matching session identity, one accepted remote peer, increasing probe counters, and bidirectional ping/pong. Record both exported logs, target/game/content fingerprints, package hash, and actual process/runtime versions.

Send several reliable probes in both directions. Verify identical received ordering, then Leave/rejoin and repeat. Test focus loss, scene transition and mod disable/enable for bounded cleanup. Repeat mismatched-manifest rejection with a deliberate test manifest. Preserve the exact logs; only these two-process observations establish loading and exchange in Unity 5.0.0f4/its actual Mono. No standalone test result should be relabeled in-game verification.
