# Authority replication foundation

`src/MSCMP.Sync` connects the pure authority model to the pinned UDP transport without using Unity objects. It is not automatically constructed by the in-game diagnostics panel.

`WorldSyncSession` requires an already started network role. The host supplies an `AuthorityWorld` with the network's generation and its ready local host. A client supplies no authority. Transport acceptance creates a bound link; the host connects an unready player and queues an immutable baseline. The client validates it, acknowledges the watermark, applies catch-up, and requests readiness. The host serializes the Ready commit with other changes. Live commits follow that grant on the same ordered stream.

Requests bind actor/connection IDs from the link, and the host validates readiness, sequence, entity state, revision/epoch, hand occupancy, distance and release bounds. Competing pickups produce one owner. Rejected/repeated commands do not produce another effect. Rendering and actual game mutations must only follow successfully applied commits.

Bounds:128KiB per blob/queued bytes,350-byte chunks,32queued blobs, one receive buffer per link,90-second overall join deadline, maximum3baselines,120-second partial-transfer expiry. Snapshots/events have additional explicit count/text bounds. Backpressure retains unsent chunks. Overflow, missing retained history, malformed state or expired join disconnects that peer rather than enabling partial state. Disposal releases ownership and disconnects its bound links; an old world cannot advertise Ready on a restarted transport generation.

`tests/SyncTests.cs` uses real localhost UDP with one host and three clients. It exercises baseline/catch-up/Ready, competing pickups, throw state, late join while held, disconnect release, malformed-peer isolation, sender binding, truncation/allocation limits, stalled bootstrap and teardown. Synthetic test entities and coordinates do not establish game bindings.

Unfinished integration: actual item/controller/FSM adapters, host physics, user-facing uncertain-command reconciliation, a scalable independent movement stream, full vanilla bootstrap and coherent game saves. The preview avatar relay and world framing are currently separate consumers; a production dispatcher must multiplex them before enabling both together.
