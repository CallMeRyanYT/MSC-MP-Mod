# Architecture

## Scope and evidence

Version 0.2.0 includes diagnostics, legacy UDP, an optional visual avatar preview, and tested pure authority/replication/checkpoint foundations. Vanilla shared-world gameplay is disabled. A successful compile does not prove that the loader can execute the assembly. See `COMPATIBILITY.md` for inspected facts, `docs/FEATURE_SUPPORT.md` for support gates, and `docs/TESTING.md` for runtime evidence still required.

The module table records implemented boundaries. The remaining sections describe the target gameplay architecture; they must not be read as claims that its game adapters exist. Static player/FSM/save evidence is recorded separately in `docs/GAMEPLAY_EVIDENCE.md`.

## Boundaries

Keep a few cohesive modules; separate interfaces do not require a project for each class.

| Boundary | Responsibility | Current scope |
| --- | --- | --- |
| Mod entry and lifecycle | Register supported loader callbacks; create and tear down scene-scoped resources | Diagnostic implementation |
| Diagnostics | Bounded lifecycle trace and visible runtime state | Diagnostic implementation |
| Game bindings/adapters | Verified scene entities and FSM integration | Read-only inspected walking-player binding; no item mutation hooks |
| Transport/protocol | Bounded packets, connection state and delivery | Pinned LiteNetLib 0.7.7.2; headless-tested protocol 2 |
| Authority/state | Host entity registry and serialized transactions | Pure item model, replay, snapshots and strict replicas implemented/tested |
| Presentation/assets | Remote avatars and interpolation | Original procedural visual preview; runtime unverified |
| Persistence/bootstrap | Coherent snapshots, catch-up and recoverable data | Small pure-world UDP bootstrap and mod-only checkpoint store; vanilla integration pending |

Loader setup registration and scene initialization are separate. Scene-owned subscriptions/resources must be disposed once on exit and rebuilt once on re-entry. Process exit must also dispose remaining resources. Both paths must be repeatable without accumulating handlers. Static inspection of callbacks is evidence of available APIs; the actual game transition test remains the proof of behavior.

Unity objects and FSMs are accessed only on the Unity main thread. A future transport may parse bounded data on a worker and queue records, but must not invoke adapters there. Queues must have explicit limits and overload behavior. Use cached scene bindings rather than whole-world reflection scans each frame.

## Identity before synchronization

The host owns one registry entry per replicated entity. The minimum logical key is `(worldId, sessionGeneration, entityId)` with archetype, lifecycle and semantic revision. Persisted identity belongs to the world; an in-memory instance reference is only the local binding. Identity values are never recycled within a session. Removed identities retain tombstones until their session can no longer send applicable messages.

For authored objects, prefer inspected persistent/authored identifiers. Otherwise build a version-specific resolver from scene identity, semantic role and archetype with evidence that it is unambiguous. Names, instance IDs, mutable hierarchy paths, discovery order and coordinates alone are not cross-machine identity. The host publishes mappings; every peer proves a unique compatible local binding. Missing or ambiguous mappings fail the affected join, with a diagnostic, before interaction.

The host allocates dynamic IDs and uses an inspected typed factory to create replicas. Clients must suppress equivalent independent spawns. The registry includes lifecycle transitions and destruction; an old pose cannot recreate a consumed/destroyed entity. Before enabling this system, prove that two identical-looking objects stay distinct after movement and reparenting on independently launched clients.

## Authority and atomic relationships

The host owns shared world state, progression and rigidbody simulation. Its local player uses the same request validation path as remote players. Connection identity, readiness, entity/revision, distance, eligibility and supported feature gates are checked before acceptance.

The initial movement exception permits local controllers to propose player poses; the host validates them and sends accepted/corrected poses back to the owner. The owner must apply corrections to its real controller before interaction distances can be trusted. This exception gives no authority over items, currency, vehicles, or spawn/destruction.

An item has one mutually exclusive semantic state: free, held, installed, contained, consumed or destroyed, as applicable to its inspected adapter. Item holder and player hand are one transaction; initially a hand holds at most one item. Installation similarly updates part, slot and prior holder together. Seats have one occupant. Continuous pose sequences are separate from semantic revisions, so movement alone cannot invalidate an otherwise valid pickup.

Every accepted request has an identity and one ordered committed result. Repeated requests return the same effective outcome. After a deduplication entry is evicted, an obsolete sequence is rejected; it cannot become a fresh transaction. A missing acknowledgement means unknown outcome: reconcile against authoritative state instead of treating timeout as rejection.

Adapters that complete over several frames reserve affected state and retain a correlated pending operation until completion. Do not publish success before the resulting game state is known. Define bounded timeout and compensation/recovery before enabling an adapter. Replication guards survive deferred FSM callbacks, so applying a committed result cannot send a new request or repeat an effect. Remote requests carry an explicit actor context; do not impersonate the remote actor by rewriting shared local-player globals across frames.

## Physics and presentation

Only the host simulates supported shared bodies authoritatively. Client replicas retain required rendering and raycast behavior while suppressing inspected simulation and mutation paths. Store prior component settings so teardown can restore them. Do not disable whole FSMs or clone the local-player hierarchy without inspection.

Held items use one authoritative holder/anchor relationship and an ownership epoch. Their pose is parent-relative to that anchor; free-body motion is suspended while held. Release validates pose/velocity, clears both hand/holder references, advances the epoch, and restores host simulation. Old held poses cannot reattach a dropped item. Any visual proxy is noninteractive and unsaved; it is not a second gameplay entity.

Remote avatars use dedicated original/licensed assets, without local input, camera, audio listener, HUD or save components. Initial replicas may be visual/kinematic, with that collision limitation disclosed. Interpolate timestamped snapshots; use bounded extrapolation followed by an explicit freeze/recovery policy. Teleports and respawns reset interpolation. Rates and buffer sizes are measured tuning choices.

Vehicle permissions follow a confirmed exclusive seat and a separate control epoch. Each grant/regrant invalidates earlier controls, even for the same player. Define stale-input braking/neutral behavior from the inspected vehicle before driving is enabled. Assembly is a graph of stable part/slot/fastener identities, not a set of transforms.

## Join, readiness and reconnect

Planned states: `Disconnected -> Connecting -> CompatibilityCheck -> PreparingWorld -> ReceivingSnapshot -> ApplyingSnapshot -> CatchingUp -> Ready`. Failure or leave goes through teardown. Only Ready peers submit gameplay requests. Loading the client's arbitrary single-player world does not establish a host baseline.

Capture a coherent host snapshot at a known tick and committed-event watermark. Include catalogue, mappings, dynamic factories, tombstones, relationships, revisions and all supported gameplay state. Retain subsequent commits while bounded chunks transfer. Apply catalogue/spawns before dependent relationships, replay the retained tail exactly once, and complete an ordered ready/acknowledgement barrier with no missing interval. Motion received during bootstrap can be coalesced by entity/epoch, but cannot replace required semantic events. Tail overflow, unknown dependency, checksum failure or timeout triggers bounded retry or a clear failed join; never partial readiness.

Reconnect authenticates a host-issued token, replaces the old connection generation, cleans its avatar/ownership once, and initially uses a full snapshot. Display names are not identity. Old-generation packets are discarded. The first release does not migrate hosts; host loss ends the session without promoting a replica to authority.

## Persistence isolation is a gate

Use disposable test saves until every relevant write path is inspected and isolation is proved. A second game installation, one redirected file, or a backup does not establish isolation. Discovery must cover ordinary save, any autosave, quit/scene callbacks, player files, settings dependencies and third-party writers. Never delete a personal save to make joining work.

The intended host profile owns vanilla world files plus mod metadata in one recoverable generation. Capture both from the same logical checkpoint, briefly quiescing transactions if necessary. Write and validate a full staging generation, then publish its manifest/pointer; retain the previous coherent generation. A crash between components must recover the previous generation rather than mix identities and world contents. Exact paths and hooks remain unverified until inspection proves them.

Bootstrap uses an isolated session location and an explicit file allowlist with bounded payloads. No packet supplies arbitrary filesystem paths. If temporary local file changes are unavoidable, require a recovery journal and tested startup restoration. Live connection ownership/seat occupancy is not persisted as an active player; normalize held items through an explicit offline policy. Leaving cancels queues, restores component settings and clears replicas. Whether a scene reload or restart is required before single-player is a runtime finding, not an assumption.

## Gates and concrete design risks

1. **Loader lifecycle:** callback signatures do not prove menu/scene timing. Re-entry and quit traces must show one setup/cleanup per generation.
2. **Legacy runtime:** modern compiler output can still reference unavailable APIs. Select and pin transport only after inspecting dependencies and loading it in MSC; current package compatibility is not assumed.
3. **Mutation interception:** a post-pickup notification is too late to prevent client mutation. Inspect the pre-mutation path and deferred completion before implementing transactions.
4. **Save boundary:** networking and avatar development must not advance into ordinary saves before isolation evidence exists.
5. **Identity ambiguity:** resolver failure must stop joining rather than bind the nearest or first matching object.
6. **Cross-channel ordering:** reliable transport alone neither makes effects exactly once nor creates a snapshot readiness barrier.
7. **Scope leakage:** unsupported world changes must be blocked when a future multiplayer session is active. Current diagnostic mode has no multiplayer session and does not promise to gate vanilla gameplay.

The first playable acceptance target remains two visible players sharing one inspected item family through pickup race, drop, late join and disconnect. Four-player, vehicle, assembly and wider-world support each require their own evidence before exposure.
