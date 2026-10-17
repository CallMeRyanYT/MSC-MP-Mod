# Implemented protocol and remaining scope

The outer diagnostic transport is **protocol2**, implemented in `src/MSCMP.Network/Protocol.cs`. [TRANSPORT](docs/TRANSPORT.md) is the precise field/limit reference. Current code replaces the earlier speculative packet sizes in the project brief.

## Transport

Packets are little-endian with strict UTF-8, explicit types and lengths, finite float validation and no object deserialization. The44-byte envelope binds session GUID, session generation, connection generation and stream sequence. Total payload+envelope is at most480bytes; application data is at most384bytes. Transport fragmentation/merged messages are rejected by the pinned ingress guard.

Hello/Welcome compare protocol, mod version, actual game/content fingerprint, loaded-mod manifest and capabilities. A host issues cryptographically generated reconnect tokens and monotonically increasing player/connection identities. Admission is limited to three remote peers. Compatibility is not encryption or public-server authentication; initial use is private LAN/tailnet.

Reliable probes and application data each have a distinct monotonic ordered stream. Poses use sequenced freshness. Ping/pong timestamps use a monotonic clock. Stale session/connection traffic is discarded. Callback dispatch to Unity-facing code occurs only during main-thread Poll. Counts, queues, message rates, handshake time and token retention are bounded.

## Authority world messages

`src/MSCMP.Sync` transports a pure authority world. Version 0.3.0 explicitly attaches it to the coffee-cup and Hayosiko-body adapters in disposable shared mode; those Unity adapters are not yet runtime-verified. SharedWorldV2 capability bit 4 must match during admission, alongside networking bit 1 and avatar appearance bit 2. Diagnostic mode omits bit 4.

- Commands contain kind, world generation, request sequence, entity ID, expected revision/ownership epoch and bounded vectors. Actor and connection IDs are injected from the accepted transport link.
- SMW2 snapshots carry complete entity/player records, tombstones, snapshot ID and commit watermark. Entity records include position/velocity, a validated unit quaternion and angular velocity. Authored mapping and replica graph validation precede readiness.
- Baseline ACK, catch-up and ReadyRequest form an ordered readiness barrier. The Ready grant is queued before live commits. No client command is accepted by authority before readiness.
- Commit records contain complete affected entity/player states in one transaction. Request results correlate outcomes without granting client authority.
- Application blobs use explicit transfer ID/total/offset/count framing in350-byte chunks. At most128KiB per blob/queued byte budget,32queued blobs and one active receiver buffer per peer. Sender backpressure retains its cursor. Whole bootstrap has90seconds and at most3baselines; a partial blob also expires after120seconds. This is a deliberately small world slice, not general bulk save transfer.
- Replay caches plus a retained high-water mark prevent old requests executing after eviction. A timeout is not proof of rejection. The adapter still needs a user-facing uncertain-outcome/retry workflow.

## Avatar preview

Visual relay uses MAV2 magic/version and fixed 26-byte removal, 27-byte appearance or 42-byte pose messages. Host-bound peer IDs are relayed only by the host. Clients accept relays only from their accepted host connection. Per-actor generations, pose sequence checks and bounded removal tombstones prevent stale resurrection. World framing skips known avatar and vehicle namespaces; their own decoders validate them. An actual UDP regression verifies that interleaved namespaces preserve world readiness.

Local poses are sampled at10Hz; original figures interpolate an eight-sample buffer100ms behind and hide after2seconds stale. This is not host-validated movement, collision or controller correction.

## Vehicle messages

SMV1 messages are bounded to 64 bytes and carry kind, seat revision and request/input sequence. State/result packets announce the authoritative driver; state also announces gear. Clients request seat 0 or exit -1 and send normalized controls. The host derives actor/connection from the envelope, validates readiness, distance, occupancy, held-item state, revision and freshness, and revokes input on disconnect or 500 ms without fresh input. This scope covers one Hayosiko driver; it is not a complete vehicle-state schema.

## Not implemented as game protocol

Vanilla save bootstrap, dynamic game factories, other item/vehicle families, passenger seats, assembly/fastener transactions, needs/death, economy/jobs, time/weather/NPC simulation, host migration, public-server authentication and a large-world bulk channel remain unfinished. The complete intended semantics remain in `docs/PROJECT_BRIEF.md` and `ARCHITECTURE.md`.

Headless tests prove byte and domain behavior. Actual Mono loading, two-game-process exchange, save isolation and game convergence require independent runtime evidence.
