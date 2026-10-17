# Mod checkpoint foundation

This module persists only the immutable `WorldSnapshot` records owned by MSCMP. It does not discover, read, redirect, back up, or write My Summer Car's save files. It is not evidence that vanilla saves and mod state can yet be captured together, or that a client cannot overwrite its single-player save. Those game-integration gates remain unverified.

## Implemented APIs

- `SnapshotCodec.Encode` / `Decode` serialize one complete coherent snapshot. `CheckpointLimits` bounds total bytes, entity/player counts, and each UTF-8 string. The default disk limits are 16 MiB, 100,000 retained entity identities including tombstones, 64 player identities, and 1,024 bytes per string. The domain also limits identity labels to 256 characters. Callers can choose smaller budgets.
- `CheckpointStore.Save` publishes a validated snapshot to an explicitly supplied dedicated directory. It keeps the previous coherent checkpoint when replacing an existing valid checkpoint.
- `CheckpointStore.ReadCheckpoint` reads diagnostic checkpoint data unchanged, falling back to the previous coherent file if the current file is missing or invalid. This result must not be installed as a live host session.
- `CheckpointStore.RecoverForHost` persists a new session generation under the filesystem limits below, normalizes offline ownership, publishes that normalized checkpoint, and only then returns it. Every successful recovery receives a generation greater than every previously reserved generation in that store.
- `CheckpointRecovery.Rebase` provides the pure normalization operation. Standalone callers must supply a strictly newer generation that they have already reserved durably. Use the store recovery method for runtime startup.

The code targets the verified legacy C# 3 / .NET Framework 3.5 profile and uses no Unity or game-owned assemblies. It is a foundation awaiting integration with a verified world restore adapter.

## Binary format, version 2

All integers are unsigned little-endian. Floats are IEEE 754 32-bit values. There is no runtime-type serialization or executable payload.

| Field | Encoding |
| --- | --- |
| Magic | Four ASCII bytes `MSCP` |
| Schema version / flags | `uint16` version 2, `uint16` flags 0 |
| Payload length | `uint32`, exact bytes excluding the 12-byte header and trailing digest |
| Generation / snapshot ID / commit watermark | Three `uint64` values |
| Entity count / player count | Two `uint32` values, checked before allocation |
| Entities | Sorted by network ID; ID, semantic revision, ownership epoch, motion sequence as `uint64`; lifecycle as `uint8`; holder ID as `uint64`; authored key and archetype as bounded strings; position and velocity as three floats each; unit quaternion as four floats; angular velocity as three floats |
| Players | Sorted by player ID; ID, connection generation, semantic revision, pose sequence, hand entity ID as `uint64`; connected and ready as exact `uint8` zero/one; position as three floats |
| String | `uint16` UTF-8 byte count, then strict UTF-8 bytes without terminator |
| Integrity | SHA-256 over the entire header and payload, 32 trailing bytes |

The generation on each decoded entity comes from the snapshot envelope. Unknown versions, flags, enums, malformed Unicode, non-finite floats, inconsistent lengths, excess records, duplicate identities, unresolved holder relationships, and trailing bytes are rejected. A fresh `ReplicaWorld` validates both sides of every hand/holder relationship before the codec accepts a snapshot. SHA-256 detects corruption; it is not sender authentication or protection against someone deliberately modifying a local file and recalculating the digest.

The codec can be reused by a future network snapshot implementation. Its 16 MiB disk ceiling does not establish network bootstrap support. Current transport payload/rate limits require a separate bounded chunk protocol, acknowledgements, timeouts, cancellation, checksums, and traffic scheduling. No bulk-transfer or live join guarantee is implied by this module.

## Publication and recovery

The dedicated directory contains `world.mscp`, `world.previous.mscp`, `session-generation.mscg`, and an empty `checkpoint.lock`. The generation lease has its own `MSCG` magic, version/flags, `uint64` generation, and SHA-256 digest.

Writers first validate/encode the complete immutable snapshot, then acquire an exclusive filesystem writer lock. Each publication writes a uniquely named temporary file in the same directory using the FileStream overload available in the shipped Mono profile, flushes its managed buffers, and uses the filesystem's atomic replacement operation. First publication uses a same-directory move. There is deliberately no delete-then-move fallback. The selected local filesystem must support atomic replacement. This implementation has not been subjected to physical power-loss testing and does not force storage-device flushes or promise survival of physical power loss. Generation monotonicity assumes that successfully published filesystem records survive; this is not a power-loss durability guarantee.

Readers select only the named current/previous files. A crashed writer's abandoned temporary file is ignored; it is never automatically promoted into a committed generation. Ordinary `Save` refuses to overwrite a corrupt current checkpoint, preventing corrupt bytes from replacing the good previous backup. Explicit host recovery can instead restore from the validated backup while preserving it.

Recovery reserves the next generation before publishing the restored state. An interruption between those steps consumes a generation; the next recovery skips it. A corrupt or missing generation lease causes recovery to fail closed, because falling back to an older lease could reuse a previously live generation. Keep the lease alongside checkpoint backups. Restoring an older copy of the entire directory is outside this local monotonicity guarantee and requires a separately established new session identity before use.

All players recover disconnected, unready, and with empty hands. A previously held item becomes free at its holder's latest accepted saved position with zero velocity; its semantic revision and ownership epoch advance once. Repeated recovery of that already-normalized checkpoint advances the session generation but does not release the same item again. Stable entity IDs, authored mappings, terminal tombstones, and supported free-item state remain present. Previous connection generations remain offline metadata, never authorization to revive a connection.

## Validation and remaining integration work

`tests/PersistenceTests.cs` exercises canonical round trips, every truncation point and single-byte corruption of a representative checkpoint, bounded counts/strings/bytes, malformed records with recomputed checksums, duplicate identities and broken hand graphs, replacement and backup preservation, writer contention, interrupted temporaries, corrupt-current fallback, durable generation reservation, and repeated held-item recovery. Fixtures are created under the generated test output directory. No real game saves are involved.

To complete the game's persistence milestone, inspect and control every actual game/dependency save entry point, prove client isolation, capture vanilla and mod state at one logical mutation barrier, publish them as one recoverable generation, restore entities through verified factories/mappings, and run interruption plus save/reload tests inside the game. This module does not satisfy those missing gates by itself.
