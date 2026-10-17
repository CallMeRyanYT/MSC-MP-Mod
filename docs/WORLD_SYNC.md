# Authority replication foundation

`src/MSCMP.Sync` connects the pure authority model to the pinned UDP transport without using Unity objects. In 0.4.0, the panel constructs the explicit persistent-item/Hayosiko game adapter when Shared world is selected and the disposable-profile guard passes. Diagnostic mode does not construct it.

`WorldSyncSession` requires an already started network role. The host supplies an `AuthorityWorld` with the network's generation and its ready local host. A client supplies no authority. Transport acceptance creates a bound link; the host connects an unready player and queues an immutable baseline. The client validates it, acknowledges the watermark, applies catch-up, and requests readiness. The host serializes the Ready commit with other changes. Live commits follow that grant on the same ordered stream.

Requests bind actor/connection IDs from the link, and the host validates readiness, sequence, entity state, revision/epoch, hand occupancy, distance and release bounds. Competing pickups produce one owner. Rejected/repeated commands do not produce another effect. Rendering and actual game mutations must only follow successfully applied commits.

Bounds:128KiB per blob/queued bytes,350-byte chunks,32queued blobs, one receive buffer per link,90-second overall join deadline, maximum3baselines,120-second partial-transfer expiry. Snapshots/events have additional explicit count/text bounds. Backpressure retains unsent chunks. Overflow, missing retained history, malformed state or expired join disconnects that peer rather than enabling partial state. Disposal releases ownership and disconnects its bound links; an old world cannot advertise Ready on a restarted transport generation.

`tests/SyncTests.cs` uses real localhost UDP with one host and three clients. It exercises baseline/catch-up/Ready, competing pickups, throw state, late join while held, disconnect release, malformed-peer isolation, sender binding, truncation/allocation limits, stalled bootstrap and teardown. Synthetic test entities and coordinates do not establish game bindings.

The item/body adapters use SMW2 entity records with orientation and spin. World framing skips MAV2 character and SMV1 vehicle messages, allowing their dedicated consumers to share the ordered application stream. Automated UDP checks cover this routing. Unfinished integration includes other item/vehicle families, a scalable independent movement stream, full vanilla bootstrap and coherent game saves. Actual item/van behavior still awaits game validation.

## Version 0.4.0 additions

SharedItemBinding resolves 22 persistent objects from initialized save/load references, including the two axes and two bucket/dipper pairs. World readiness requires the exact catalogue plus van. All mapped item use FSMs and trigger colliders are scoped off; captured flags restore at teardown. This is pickup/body support, not refuelling, cooking or tools. See SHARED_ITEM_CATALOGUE.md for the exact contract.

Live events are batched at up to 16 commits every 50 ms per connection. The game adapter samples at 10 Hz and publishes at most eight changed items per sample with round-robin fairness, plus the van. All records retain their original ordered watermarks; no semantic event is dropped to achieve batching. Backpressure preserves the cursor.

Pending item requests retry the original immutable command once per second. Duplicate replies are ignored; a correlated result must also reach its commit watermark before input unlocks. A timeout remains uncertain and does not release the local hand speculatively.

Shared mode owns the single 10 Hz pose source. Host validation produces a receipt correlated to the transport pose sequence. Rejected positions reconcile the local root using bounded sent-pose history; subsequent local displacement is retained up to eight metres, otherwise snapped to the accepted position. Pre-correction samples are discarded to prevent repeated correction. Remote characters use accepted host positions. This does not establish terrain-aware anti-cheat, death/respawn or vehicle prediction.
