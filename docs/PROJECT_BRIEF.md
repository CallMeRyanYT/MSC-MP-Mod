# My Summer Multiplayer — AI implementation prompt

Use this document as the project's complete implementation specification. Build the mod through the milestones below, keeping the entire specification available for later phases.

## Project inputs

- Game: My Summer Car on Windows.
- Working project name: MySummerMultiplayer; keep the repository's existing name if one is already established.
- Game installation path: detect locally, or ask me for this exact path if it cannot be found.
- Mods directory: detect from the installed MSCLoader configuration.
- Game and loader versions: inspect and record; do not guess.
- Source directory: use the current workspace/repository.
- Initial test target: one host and one client, then four players.
- Second-player test availability: unknown; prepare precise manual test steps when a second process or PC is unavailable.
- Connection model: host/client over LAN or Tailscale with direct-IP joining.
- Main requirements: visible characters; shared item pickup, holding, drop, and throw; consistent world state; later vehicle driving, assembly, and progression.

These inputs describe the intended build. Do not assume my machine already has the required tools merely because setup instructions were provided. Inspect what is available and report only the specific missing prerequisite that blocks the current milestone.

## Part A — Role, objective, and working rules

You are the lead developer implementing a cooperative multiplayer mod for **My Summer Car on Windows**. Work inside the supplied project and use the user's installed game as the integration target. Temporary project name: **MySummerMultiplayer**.

Build a real, maintainable mod through playable milestones. The end goal is that players see one another as characters, inhabit the same authoritative world, share items and vehicles, assemble the Satsuma together, and continue a shared save. Start with two players, then validate four. Use a host-and-client model with direct-IP LAN/Tailscale connections. The host is a participating player's running game.

Use these rules throughout:

1. Inspect the actual game, loader, assemblies, logs, and PlayMaker state machines before naming hooks or depending on behavior. Record evidence. Do not invent game APIs, scene paths, object names, or FSM transitions.
2. Follow the installed MSCLoader's lifecycle. Read the maintainer's API documentation. Keep setup registration separate from scene initialization and handle scene unload/reload explicitly.
3. Implement working code and build scripts, not only architecture documents or pseudocode. Finish each bounded milestone before extending the supported feature surface.
4. Never claim zero desync, complete game support, a successful build, or a passed multiplayer test without evidence. Distinguish implemented, built, unit-tested, and verified in-game.
5. Prefer the simplest architecture that enforces the required invariants. Keep transport, protocol, authority, game adapters, presentation, and persistence separate. Do not build a new general-purpose networking engine as the first task.
6. Treat shared object identity, authority, persistence, and join bootstrap as foundations. Do not postpone them until after position synchronization.
7. Use the whole specification as the design context. Execute it through the milestone sequence in Part O, preserving later requirements without prematurely implementing every subsystem.
8. Continue routine implementation and fixes independently. If local runtime access is unavailable, complete the work that can be verified and ask for the smallest specific log or test result that blocks the next integration step. Do not label an untested milestone complete.

**First deliverable:** a compatibility report, reproducible minimal mod build, and a diagnostic screen/log proving that the mod initializes and cleans up correctly in the actual game.

If a second PC or interactive game session is unavailable, mark the corresponding gate **awaiting manual test**. Continue independent implementation and automated verification that do not rely on unverified integration assumptions. Supply exact host/client steps and the logs needed to assess the result. Do not stop all useful work because one manual test is pending, and never count that test as passed.

## Part B — Runtime discovery and dependency proof

Inspect and record:

- Installed game build/version and the identifiers/hashes needed to distinguish supported gameplay code.
- Unity version, Mono/runtime profile, process architecture, and available assembly references.
- MSCLoader version, selected game branch, lifecycle callbacks, supported FSM hooks, and existing dependency versions.
- Actual scene entry/exit behavior, local player/controller structure, interaction code, and relevant PlayMaker FSMs.
- Save paths, save/load triggers, background execution and pause behavior, and initial scene object construction.
- Installed gameplay mods that could change shared simulation or entity mappings.

Use local game references such as `UnityEngine.dll`, `Assembly-CSharp.dll`, and `PlayMaker.dll` only if present and required. Resolve their actual paths; do not assume a newer Unity assembly layout. Avoid copying game-owned assemblies into release packages.

The [current maintainer guide](https://github.com/piotrulos/MSCModLoader/wiki/Install-Visual-Studio-for-MSCLoader) uses .NET Framework 3.5 for this workflow. Select compiler syntax and dependencies compatible with the verified target. A recent compiler or a modern test process does not make modern runtime APIs available inside the game.

Put networking behind an interface. A legacy-compatible LiteNetLib build is a candidate, not an assumed working dependency. The [current LiteNetLib 2 documentation](https://github.com/RevenantX/LiteNetLib) requires .NET Standard 2.1 and Unity 2021.2 or later, so do not install its latest package blindly. Inspect a candidate's source/framework targets and pin the exact validated version or commit. If it is unsuitable, assess a small, established alternative and document the tradeoff.

Prove the chosen transport by loading it in MSC and exchanging bounded ping/pong and reliable messages between two actual game processes. A successful standalone .NET console test alone is insufficient. Check all transitive references and any native-library architecture requirements.

Use the loader's PlayMaker extensions for scoped FSM integration. Its [API documentation](https://github.com/piotrulos/MSCModLoader/wiki/MSCLoader-API) explicitly warns against Harmony patches of PlayMaker action classes. Use any patching elsewhere only for an inspected, justified target with tested cleanup and compatibility checks.

Create `COMPATIBILITY.md`, `ARCHITECTURE.md`, `PROTOCOL.md`, and a local-path configuration example. Keep machine-specific paths, saves, logs, credentials, generated dumps, and game binaries out of Git.

## Part C — Authority and world consistency

The host owns canonical shared-world state, shared progression, and shared rigidbody physics. Clients send requests. The host validates them, performs accepted changes once, and publishes committed state. The host's own interactions use the same authority path as remote interactions.

For the first co-op milestone, a local player's movement controller may supply poses which the host validates and accepts. Document this as a scoped player-movement exception. It does not authorize the client to change shared item ownership, spawn objects, award money, alter vehicles, or overwrite the world.

Enforce these invariants:

- One authoritative record exists for each network entity.
- An item has at most one holder. A held item and the holder's hand slot reference each other consistently.
- A player can hold only the supported number of items, initially one.
- An installed part belongs to one valid mounting slot; a slot accepts only its supported occupancy.
- An item cannot be held, installed, and consumed simultaneously.
- A seat has at most one occupant, and driving permissions follow the confirmed driver seat.
- Each accepted transaction has a unique identity and an ordered committed result.
- The same request repeated by the network cannot duplicate an item, payment, sound-triggering action, or other one-time effect.
- A client cannot overwrite a newer semantic state with a stale event or motion packet.
- A joining client cannot interact until its world baseline and catch-up are coherent.
- Client sessions cannot silently write the host's world over the client's personal save.

Do not run independent authoritative physics on every machine and hope that identical inputs or seeds keep them identical. Remote shared bodies are replicas, with simulation and interaction behavior controlled by their adapters. Preserve whichever components are needed for rendering, raycasts, or supported local behavior.

Keep a feature-support matrix. Unsupported world-changing interactions must be clearly gated in multiplayer instead of silently running independently on clients. Cosmetic behavior can remain local when it has no authoritative consequence.

## Part D — Entity identity, matching, and lifecycle

Build an explicit host-owned entity registry before broad synchronization.

Each replicated entity needs a network ID, type/archetype, lifecycle state, and semantic revision. Track the session/world generation and, where relevant, an ownership epoch. Keep pose sequence numbers distinct from semantic revisions: ordinary movement must not invalidate every pickup request.

For scene-authored objects, build a verified resolver using stable authored/persistent identifiers when available. If those are unavailable, combine scene identity, semantic role, archetype, and version-specific evidence into a resolver, then detect ambiguity. The host sends the resulting mapping; clients must prove that each mapping refers to the intended local object.

Never use any of these alone as cross-machine identity: `GetInstanceID()`, runtime hash codes, object names, mutable transform paths, current coordinates, collection indices, or the order objects were discovered. Two objects can share a name, and attachment can change a hierarchy. Establish identity before such changes and preserve it.

For dynamically created items, the host allocates the network identity and sends a typed spawn record. Clients create the corresponding replica from verified local resources. Suppress independent client spawns from shops, crates, item consumption, containers, and similar systems. Record a supported factory for each dynamic archetype.

Define spawn, map, hold, release, install, uninstall, consume, break, destroy, and restore transitions as applicable. Represent destruction with lifecycle state/tombstones so late packets cannot resurrect removed objects. Do not silently recycle identities within a session. Persistent identities and the registry must survive supported save/reload operations.

When mapping fails, report the missing or ambiguous entity and stop the affected join/feature. Guessing the nearest object is not acceptable reconciliation.

**Proof:** two identical-looking objects remain distinct across two independently launched clients; one can move, be picked up, and be reparented without the other being affected.

## Part E — Transport and protocol

Implement Host, Join, Leave, connection status, and a configurable endpoint/UDP port. Keep the game transport independent of Steam matchmaking. Tailscale provides connectivity; the mod provides sessions and state replication.

The handshake must negotiate protocol version, mod version, supported game fingerprint, relevant dependency/content manifest, world/session identity, and peer capabilities. Reject incompatible combinations with a useful message. Do not require identical cosmetic settings unless they affect shared state.

Bind player identity to the accepted connection. Do not trust a packet's claimed sender ID. A reconnect uses a defined identity and host-issued session token, not merely the same display name.

Define a versioned binary schema with explicit message types and maximum sizes. Include, where applicable, session generation, entity ID, request ID, semantic revision, ownership epoch, sequence number, and host tick. Document numeric encoding and bounded string/array lengths. Validate enums and reject non-finite position/rotation/physics values.

Use delivery modes appropriate to the data:

| Data | Intended behavior |
| --- | --- |
| Handshake, joins, grants, denials, lifecycle and progression transactions | Reliable, with defined ordering and duplicate handling |
| Frequent poses, velocities, aim, and vehicle motion | Sequenced freshness; obsolete updates may be discarded |
| Initial world snapshots and repair snapshots | Reliable bounded chunks on a bulk path |
| Purely cosmetic transient events | Delivery policy chosen per effect |

Reliable transport does not provide exactly-once gameplay by itself. Implement application-level request deduplication and idempotent state application. Separate bulk transfer from time-sensitive events where the transport supports independent channels. Define what happens when dependent messages arrive across channels in the wrong order.

Start with self-contained motion/state records. Add delta compression only after baseline acknowledgement and recovery are implemented. Sequence wraparound, stale sessions, unknown entities, disconnects, queue overflow, malformed packets, and excessive message rates need bounded handling.

Keep Unity object access on the Unity main thread. Worker threads may parse or queue bounded data, but they must not mutate game objects or FSMs. Use bounded queues and avoid allocations or whole-world reflection scans every frame.

Treat the initial mode as private co-op on an authorized LAN/tailnet. If public Internet hosting is later added, select an established authenticated transport/security approach and document it. Do not invent cryptography or interpret a plaintext room password as encrypted networking.

## Part F — Shared pickup, holding, dropping, and throwing

This is the first required gameplay feature. Implement it for an explicit list of inspected item types before expanding coverage.

Use a host-owned state model such as `Free`, `HeldBy(playerId)`, `InstalledIn(entityId, slotId)`, `ContainedIn(entityId)`, `Consumed`, and `Destroyed`. Adapt the model to verified game behavior while keeping incompatible states mutually exclusive.

### Pickup transaction

1. The client's normal interaction identifies a registered entity. Intercept the local mutation before it independently changes shared state.
2. Submit a uniquely identified pickup request containing the entity identity and relevant expected semantic state/revision.
3. The host verifies connection/session identity, entity existence, lifecycle, distance, interaction eligibility, player hand availability, and any game-specific constraints.
4. Process competing requests through one serialized authority path. Reserve and commit the item and the player's hand together. If two players request the same item, exactly one succeeds.
5. Send the committed holder relationship, new semantic revision, and ownership epoch to every relevant peer. Send the requester a correlated grant or denial.
6. Apply the authoritative relationship everywhere. The original ground item must no longer be available for another pickup. Every observer sees the same entity held by the winning character.
7. A rejected or timed-out request leaves no duplicated item, permanently hidden object, stale hand reference, or invisible collider. On an ambiguous timeout, query/reconcile authoritative state; do not assume the host rejected a request whose acknowledgement was lost.

Initially, use a pending animation or UI indication while waiting. If speculative pickup visuals are introduced later, make them reversible and prohibit them from committing consumption, attachment, or other shared consequences before confirmation.

A repeated request must return the same effective outcome without applying the transaction twice. Use request identities scoped to peer/session generation and a bounded duplicate policy that also rejects obsolete commands after cache eviction.

### Holding

Represent holding as an authoritative relationship to a player and a defined hand/carry anchor. The holder's accepted pose/aim drives an approved held pose under host rules. Observers render that relationship smoothly.

Specify whether transmitted held positions are world-space or parent-relative. Do not simultaneously parent the item to a moving hand and apply an unrelated free-body transform stream. Motion packets from a previous ownership epoch must be discarded.

There must be one gameplay entity. If a local presentation proxy is necessary, it cannot be separately interactive, separately saved, or independently counted as an item. Configure collisions and raycasts deliberately so a held item cannot fight its holder's body or duplicate physics impulses.

### Drop and throw

Dropping or throwing is another host transaction. Verify current ownership and the relevant revision. The host selects/validates release transform and allowed velocity, clears the holder and hand relationship, advances the ownership epoch, restores free-body simulation, and broadcasts the result.

A late held-pose update must not reattach a dropped item. Support rapid pickup/drop cycles and drop followed immediately by another player's pickup. Impose sensible game-derived bounds on throw input; do not trust arbitrary client velocities.

On holder disconnect, the host resolves the item exactly once at a verified release location. Define the result of death, vehicle entry, scene change, and consumption while holding. Unsupported combinations should be gated until their transitions are implemented.

**Acceptance:** both clients show the same holder, the item is unavailable on the ground while held, and either player can retrieve it after a confirmed release.

## Part G — Characters, movement, and presentation

Create a dedicated remote-avatar representation. Do not blindly clone the original local-player hierarchy: identify and avoid duplicating input handlers, camera, audio listener, HUD, save logic, and single-player controllers.

Each player needs:

- Stable session identity and a display name distinct from network identity.
- A visible body, root pose, head/view direction, and a holding anchor.
- Idle, walking/running, stance, holding, and seated animation where supported.
- Spawn, disconnect, reconnect, and supported death/respawn lifecycle.
- Correct relationship to vehicle seats and other moving parents.

Use a simple original placeholder person to prove networking, then ship an original or appropriately licensed character mesh and animation set. Keep source assets and bundle-generation instructions reproducible. Build bundles with the verified Unity version. Do not make the final feature depend on a copied proprietary character from another mod.

Preserve a responsive local camera. For remote players, buffer accepted snapshots and interpolate against the estimated host clock. Handle teleports and respawns explicitly. Use bounded extrapolation for missing updates, then a defined freeze/recovery policy; never extrapolate indefinitely.

Return accepted or corrected poses to the owning client, correlated with its pose/input sequence. Define how the actual local controller applies corrections and explicit teleports. Rejected movement must not leave the owner permanently somewhere different from the position used by host interaction checks. Test an adjusted/rejected pose followed by a legitimate pickup.

State the collision policy for remote avatars. For the initial slice, visual/kinematic remote bodies are acceptable if the limitations are disclosed. Any ability to push a shared item must produce a host-side action with a supported physics consequence.

Animation parameters and head direction can use compact updates. Seat membership, held item identity, death, and respawn require reliable semantic transitions. Name tags and cosmetic choices must not change the authoritative entity catalogue unexpectedly.

Define pause, menus, focus loss, and time scaling. Opening a client's menu should not independently pause shared-world simulation. Ensure host networking and session liveness are not accidentally tied to a scaled timer that stops during a menu. Test supported background behavior instead of assuming it.

## Part H — Integrating game logic and PlayMaker

Build one adapter per supported gameplay system. Maintain a bindings report with the actual scene objects, components, FSM states/actions/variables, triggers, and persistent fields used by each adapter.

For every world-changing interaction, identify:

1. The local input/interaction entry point.
2. The host's validation and authoritative mutation path.
3. The client's suppression path for independent vanilla mutation.
4. The replica state-application path.
5. The presentation effects, such as audio and animation.
6. The dynamic spawn/despawn consequences.
7. The late-join snapshot representation.
8. The persistence and cleanup behavior.

A feature is supported only when those paths are understood and tested. Sending a transform packet does not establish support for the object's gameplay behavior.

Send semantic requests such as `RequestPickup`, `RequestSeat`, or `RequestInstallPart`. These are proposed protocol names, not assumed game APIs. Resolve the actual game calls/FSM targets from inspection. Do not broadcast every arbitrary FSM event or every global variable.

Use a scoped replication/application guard so applying authoritative state cannot generate another outbound action or repeat the authoritative consequence. Handle callbacks which complete on a later frame; a guard that disappears before a deferred callback executes is insufficient.

Inspect single-player assumptions carefully. The host's original game may refer to its local player through global variables. Provide an explicit interaction context for the remote actor, position, target, and intended action. Do not accidentally charge the host, mutate the host's personal needs, or use the host camera for a remote request. Avoid unsafely rewriting global player references across frames to impersonate a remote player.

Classify required client-side scripts and FSMs individually. Suppress non-authoritative shared simulation without disabling necessary rendering, local input, prompts, or presentation. Cache bindings after scene readiness, register hooks once, and restore/unsubscribe on session or scene teardown.

If an engine/FSM operation spans multiple frames, use a pending transaction/reservation and a bounded completion policy. Do not announce success before the resulting state is known. Define compensation/recovery for partial failure and log enough evidence to reproduce it.

## Part I — Shared physics, vehicles, seats, and assembly

### Physics baseline

The host simulates supported shared rigidbodies. Clients render controlled replicas. Replicate position, rotation, linear/angular velocity, sleep state, and any necessary semantic/attachment state with defined sequence and ownership rules.

Use thresholds, sleeping-body suppression, and configurable update rates. Starting hypotheses might be roughly 10–20 Hz for active motion and a 100–200 ms remote interpolation buffer; these are tuning inputs to measure, not performance promises or universal requirements. Preserve the game's verified physics timestep unless a specific change is justified.

Never continuously overwrite a client's active local physics body and leave its vanilla simulation free to fight the network. Preserve original component settings for safe session teardown. Handle waking, collision-related changes, and returning to single-player deliberately.

### First vehicle

Select one vehicle after inspection. Implement:

- Exclusive driver/passenger seat requests and release.
- Host-side driving permission based on confirmed seat occupancy.
- Sequenced continuous control state: steering, throttle, brake, and other supported axes.
- Discrete, replay-safe actions for ignition, shifting, doors, and switches. Repeating a packet must not shift through several gears.
- Host vehicle physics and relevant drivetrain/engine state.
- Client body/wheel/steering/engine presentation driven by snapshots and semantic state.
- Input timeout behavior, driver disconnect cleanup, and supported exit locations.

All seated occupants, including the driver, follow the authoritative vehicle/seat relationship. Suspend or reject incompatible on-foot pose updates until an authoritative exit completes. A client must not enter a moving seat locally before the authoritative claim is accepted.

Each seat/control grant establishes a new control epoch. Every driving input includes that epoch; release, disconnect, or a new grant invalidates inputs from earlier occupancy. Matching player identity is insufficient when the same player leaves and re-enters with old input packets still in flight.

Record the responsiveness cost of host-side vehicle simulation. Add prediction or delegated vehicle authority only as a later, measured milestone. If delegation is introduced, specify exclusive ownership of the connected physics assembly, epoch-based handoff, state transfer, collision consequences, timeout, and recovery. Never let two peers simulate the same shared assembly authoritatively.

### Assembly and the Satsuma

Treat assembly as a graph of part identities and mounting slots, with fasteners and gameplay state attached to explicit targets. Installing a part is an authoritative transaction involving part eligibility, slot compatibility, occupancy, and relevant game constraints.

An installation must update ownership/hand state, parent/slot relationship, physics/joints/colliders, rendering, game/FSM variables, and persistence coherently. Removing a part must reverse the appropriate state without creating a second entity.

Give fasteners stable identities relative to the appropriate part/slot. Use revision-checked commands or authoritative absolute state so duplicate tightening/loosening requests cannot apply twice. Replicate the engine/vehicle state actually required by supported assembly: for example fluids, battery state, damage, and installation status as discovered locally.

Test installation while another player tries to pick up or remove the same part, installation during late join, removal while seated/driving if permitted, and save/reload of the graph. Expand through explicit part families rather than claiming that every part is covered by one transform component.

## Part J — Join bootstrap, catch-up, and reconnect

Implement late joining as a consistency operation. A list of transforms is insufficient.

First define how a client constructs the same underlying world as the host. Inspect whether the game supports loading a staged host save or reconstructing its supported state through adapters. Do not merge the joining player's arbitrary single-player world into the host world.

For the earliest development-only tests, a manually prepared identical disposable baseline save is acceptable if the limitation is explicit. Full late-join support requires an implemented bootstrap path and client save isolation. Any host-save transfer must use an explicit file allowlist, bounded payloads, a separate session location, and the persistence rules in Part K; never accept remote arbitrary filesystem paths.

Use a state machine such as Connecting → CompatibilityCheck → PreparingWorld → ReceivingSnapshot → ApplyingSnapshot → CatchingUp → Ready. Only Ready peers may issue gameplay requests.

The host captures a coherent logical snapshot at a known tick and committed-event watermark. The snapshot includes:

- World/session generation and relevant content/binding information.
- Entity catalogue, dynamic spawn records, persistent mappings, and removed-entity state.
- Authoritative lifecycle/revisions, positions, attachments, held items, and quantities.
- Player identities, accepted states, seats, and supported lifecycle state.
- Vehicles, installed parts, fasteners, and relevant engine/gameplay state.
- Supported shared time, weather, progression, shop, economy, and other world variables.

Capture related fields at one consistent boundary, or use a versioned snapshot mechanism that guarantees the equivalent result. Retain committed changes after the snapshot watermark while chunks transfer.

The client builds mappings/spawns first, then resolves dependencies and applies state in a safe order. It catches up through the retained committed-event tail, verifies the completed baseline at a defined watermark, and enters Ready through an ordered commit/acknowledgement barrier. Updates arriving during that transition must be queued and applied without a gap or double application. Continuous motion can advance through fresh compatible snapshots without corrupting semantic state.

Use snapshot IDs, chunk bounds, checksums, transfer timeouts, cancellation, and bounded buffering. If the retained event tail overflows or a required dependency cannot be resolved, restart cleanly or fail with diagnostics. Never mark a partially initialized client ready.

A reconnect must distinguish the old connection from the replacement and reject packets from old generations. Restore player identity only through the defined reconnect policy. Clean up an old avatar and outstanding ownership before introducing the replacement. The host may use a full snapshot first; incremental reconnect can wait.

**Required test:** while a slow client joins, another player picks up an item, spawns/consumes an item, or installs a part. The joining player must see the committed final state exactly once after readiness.

## Part K — Saving, recovery, and session teardown

The host owns the multiplayer world's persistent state. Use a dedicated multiplayer save/profile and preserve each client's normal single-player data.

Inspect every relevant persistence entry point: ordinary saving, autosaving if present, scene transitions, quit handling, player-specific files, and dependencies that write state. Prove the client's active session cannot write into its original save. A second game folder or one redirected filename is not sufficient proof.

Keep vanilla world data and mod metadata in a recoverable generation. Metadata includes schema version, world identity, persistent entity mappings, dynamic entities, attachment graph, and supported multiplayer state. If several files cannot be atomically replaced together, write a complete generation into staging and commit a small manifest/pointer only after validation. Retain the previous coherent generation. Adapt the exact filesystem strategy to the discovered game loader.

Capture vanilla data and mod metadata from one logical checkpoint as well as publishing them coherently. If the game cannot serialize a frozen snapshot, briefly quiesce relevant mutations and settle or safely represent pending transactions while collecting the checkpoint, then resume. Saving during pickup, release, or installation must restore one valid before-or-after state, never a mixture from different moments.

Do not restore an active connection from a previous launch as a live holder. Normalize held items to a documented valid saved state or restore them through a validated offline-player policy. Clear obsolete seat ownership and network requests while preserving persistent world relationships.

Test interruption between vanilla save and metadata save. A partially written generation must be rejected or recovered from the last coherent generation rather than loaded with mismatched identities.

If bootstrap temporarily changes a client's disposable local files, implement a recovery journal and startup restoration path for crashes. Prefer a verified isolated session path. Never erase the original save to make joining work. Never overwrite unrelated settings or files received from a peer.

Leaving must stop transport activity, cancel pending work, release hooks, clear replicas, and restore required component settings. Decide whether returning safely to single-player needs a scene reload or process restart, based on actual tests. Show that requirement clearly in the UI.

No automatic host migration in the initial implementation. If the host disappears, clients end the session cleanly, retain useful diagnostics, and preserve their own data. A client must not silently become a second authority or promote an incomplete replica into a save.

## Part L — Coverage for the wider game

Maintain a support ledger with authority, integration evidence, lifecycle, late-join fields, persistence, and test status for each subsystem. Use statuses such as planned, implemented, verified, experimental, and disabled. The UI/release notes must accurately state what players can use.

Use the following initial policy decisions, adjusting them only with a documented reason:

| System | Proposed authority/behavior |
| --- | --- |
| Shared item existence, quantities, consumption | Host transactions and lifecycle replication |
| Player position/view | Local control with host validation for the first slice |
| Player hunger, thirst, fatigue, and other needs | Separate per-player state; host validates shared-world consequences |
| Vehicles, parts, seats, fasteners | Host-authoritative simulation and transactions |
| Money, purchases, shop stock | One host-owned shared-wallet policy initially, with replay-safe purchases |
| Time, day, weather | Host-owned world state |
| Sleep/time acceleration | An explicit coordinated policy; never independent time jumps on clients |
| NPCs, traffic, moving world hazards | Host-owned relevant simulation, with client replicas |
| Jobs, bills, deliveries, story triggers | Host-owned progress and transaction completion |
| Death/respawn | Explicit per-player and shared-world policy before support is enabled |
| Audio, particles, optional cosmetics | Local presentation derived from replicated state/events |
| Third-party mods | Unsupported until checked or connected through a documented adapter |

For shared purchases, commit payment, stock changes, and resulting spawn identities coherently. For consumption, update quantity/lifecycle and the intended player's needs without duplicating the outcome. For containers, define the relationship between container state and individually spawned contents.

For NPCs or traffic whose game logic references the local player, inspect how remote players affect targeting, collision, triggers, and damage. A moving visual replica alone does not establish interaction support.

Add these systems one at a time after the core slice, vehicle, and assembly gates. During development, use a clearly labelled limited multiplayer sandbox when broad vanilla gameplay is not covered. Gate unsupported world mutations rather than allowing silent divergence.

## Part M — Diagnostics and repair

Build a development overlay with peer count, session state, ping/RTT, loss indicators where measurable, sent/received bytes, snapshot age, queue sizes, entity counts, pending commands, and synchronization-repair counts.

Provide an entity inspector showing network ID, local binding, lifecycle, holder/parent/slot, semantic revision, ownership epoch, and recent relevant commands. Provide a bounded event trace correlated across machines by session ID, peer ID, request ID, host tick, and committed-event sequence.

Log binding failures, rejected transitions, stale updates, duplicate requests, timeouts, queue overflow, and repair outcomes in a useful, rate-limited way. Exclude session secrets and unnecessary private information from shareable logs. Include a small exportable diagnostic bundle with versions, feature coverage, logs, and reproduction instructions.

Detect divergence over canonical replicated state at comparable watermarks. Useful checks include entity membership, lifecycle, holder relationships, attachment slots, discrete quantities, and semantic revisions. Do not compare interpolated render positions from different times or hash uncontrolled floating-point state as if it should be byte-identical.

Support a bounded targeted repair for an entity/dependency group and a full snapshot/rejoin fallback. Repair must preserve ordering and newer revisions. Count and surface repeated repairs; do not hide a permanent integration bug behind constant state resets.

Use a repeatable network impairment harness for delay, jitter, loss, duplication, reordering, and interruption. Preserve a seed/configuration for the impairment test itself. That does not imply deterministic physics across game processes.

## Part N — Tests and acceptance evidence

Create focused automated tests for protocol bounds, semantic transitions, duplicate handling, identity/lifecycle rules, and snapshot/catch-up ordering. These are core correctness risks and merit tests. Run actual game integration tests in addition to pure logic tests.

At minimum, verify:

| Scenario | Pass condition |
| --- | --- |
| Two simultaneous pickups of one item | Exactly one winner, one entity, and matching holder/hand state |
| Same pickup/use/purchase command delivered twice | One authoritative effect |
| Grant acknowledgement lost | Client reconciles the true outcome without duplicating or abandoning ownership |
| Rapid drop followed by another pickup | One valid final owner/state; no stale reattachment |
| Pickup conflicts with install/consume/destroy | Only a valid serialized sequence commits |
| Two same-named items loaded in different discovery orders | Each maps to the correct identity |
| Holder disconnects or reconnects | One cleanup; no duplicate avatar or permanent item lock |
| Old packets arrive after reconnect | Old session/ownership data has no effect |
| Client joins while an item is held | Correct character and held relationship at Ready |
| Client joins during spawn, consume, or installation | Final entity set and attachment graph match without resurrection |
| Snapshot transfer restarts or event tail overflows | Clean recovery/failure; no partial readiness |
| Two players claim one driver seat | Exactly one confirmed driver |
| Driver input stops | Defined timeout behavior; no uncontrolled stale input stream |
| Same player exits and re-enters with delayed old controls | Old control-epoch input is rejected |
| Host adjusts/rejects a player pose, then that player picks up an item | Owner corrects its controller and interaction range agrees |
| Fastener action duplicated | Intended change applies once |
| Save/reload with dynamic and assembled objects | Persistent identities and supported relationships remain coherent |
| Save captured during pickup, drop, or installation | One logically coherent before-or-after state reloads |
| Save interrupted before commit | Previous coherent generation is recoverable |
| Client leaves and opens single-player | Original save remains intact |
| Unsupported game/mod fingerprint connects | Useful rejection before world mutation |
| Unsupported gameplay interaction is attempted | Clearly gated; no independent shared-world mutation |
| Menu, focus loss, and scene reload | Defined session behavior with no duplicated hooks or frozen liveness timer |

Use two real PCs initially. Use multiple processes on one PC only after save/config/port isolation has been proved. Repeat the applicable tests with four players before advertising four-player support.

Suggested network profiles are test inputs, not promises:

- Baseline LAN without injected impairment.
- Approximately 100 ms RTT with jitter and 1% random loss.
- Approximately 250 ms RTT with jitter and 5% random loss, plus explicit duplicate/reordered delivery cases.
- A brief complete interruption followed by recovery or a clean timeout.

Define whether the impairment tool's delay setting is one-way or round-trip. Do not accidentally double the intended RTT. Include at least one real Tailscale session, recording whether it uses a direct or relayed path if available.

For the supported slice, collect a 30-minute two-player session exercising pickup/drop, join/leave, and save/reload; repeat equivalent coverage for four players at the scaling gate. Add driving and assembly exercises as those features become supported. Record actual test duration, operations, game build, mod build, network conditions, and defects. Do not fabricate evidence when the runtime is unavailable.

Any duplicate entity, dual owner/driver, permanently stale attachment, silently missing committed transaction, or damaged client save is a release-blocking defect for the affected feature. Temporary interpolation delay under the documented network profile is not automatically a persistent-state desync; measure and describe both responsiveness and eventual convergence.

Measure CPU time, allocations, memory growth, bandwidth, snapshot size, and frame rate relative to the same game scene without the mod. Publish measured results and choose budgets from evidence. Optimize the demonstrated bottleneck rather than weakening consistency rules to obtain a misleading smooth demo.

## Part O — Milestone order and required outputs

Use this implementation sequence. A milestone is complete only when its stated behavior is verified to the appropriate level.

| Milestone | Build and prove |
| --- | --- |
| 0. Discovery | Compatibility/bindings report; minimal mod loads and unloads; build/deploy scripts work |
| 1. Networking | Pinned transport loads inside MSC; two processes handshake, exchange messages, and disconnect cleanly |
| 2. Identity and avatars | Verified small entity registry; both players see a moving temporary character; same-named objects remain distinct |
| 3. Shared item slice | One inspected item family supports authoritative pickup, hold, drop, and throw; races and duplicate requests pass |
| 4. Join and persistence | Client save isolation, coherent baseline/catch-up, reconnect, held-item cleanup, and host save/reload work for the slice |
| 5. Characters and four players | Proper character assets/animations and holding anchors; earlier tests repeated with four players |
| 6. One vehicle | Seats, remote driving, host physics, input timeout, late join, and persistence pass |
| 7. Assembly | Expand Satsuma part/slot/fastener families with transaction, late-join, and persistence tests |
| 8. Wider world | Add shops, economy, needs, time/weather, NPCs/traffic, jobs, and other features through the support ledger |
| 9. Release | Tested package, compatibility matrix, player setup guide, rollback/uninstall instructions, known limitations, and evidence |

Start designing identity, join state, and save isolation at milestone 0, even though their full implementation is verified later. Restrict early in-game work to disposable test saves until the isolation gate passes.

Keep the repository organized around a small number of clear projects/modules: mod entry and lifecycle, game bindings/adapters, network core/protocol, authority/state, presentation/assets, persistence, diagnostics, and tests. Do not create a project per tiny class without a reason.

Provide:

- A solution/project that builds reproducibly against local game references.
- Example local configuration with `MSC_GAME_DIR` and `MSC_MODS_DIR`, or documented equivalents.
- Build/deploy scripts that copy only this mod's outputs and required permitted dependencies, never overwrite unrelated mod files, and detect locked files.
- Pinned dependencies and third-party notices for anything distributed.
- A reproducible asset project/build process for the character bundle.
- Setup, architecture, protocol, compatibility/bindings, feature-coverage, testing, and known-issues documents.
- A release package containing the mod and its required redistributable assets, excluding the game's assemblies and local saves.
- A concise handoff file with the current milestone, evidence, known blockers, and exact next test.

Users installing a release need the supported game, loader, mod package, and the selected connection method. They should not need the development IDE, decompiler, inspector, or Unity Editor just to play.

## Part P — How to work and report progress

Begin by inspecting the project and installed game. State which facts are verified locally and which remain assumptions. Then implement milestone 0 and move through the sequence.

For each milestone, report:

1. The concrete behavior now implemented.
2. The main code/integration decisions and why they were needed.
3. What built, what automated tests ran, and what was verified inside the real game.
4. The exact steps for the host and joining tester to reproduce the result.
5. Remaining unsupported behavior, observed issues, and the next milestone.

Do not stop after generating empty manager classes, mocked gameplay, or a long plan. A local networking demo outside MSC cannot substitute for the in-game integration gate. A placeholder avatar is temporary. A transform-only item implementation is incomplete until the pickup/lifecycle invariants pass.

Use documentation and inspection to resolve unknowns. If a method or FSM cannot be found, explain the missing evidence and build a small diagnostic dump/tool when possible. Ask the user for a specific missing path, log, or manual test only when it materially blocks further verified work. Never claim that you accessed their machine or tested two players when you did not.

The first playable target is deliberately concrete: **two visible characters can connect to one shared test world, take turns holding the same item, drop it for one another, and remain consistent through a pickup race, a late join, and a disconnect.** Build the rest on that verified foundation.

---

# Reference notes for the implementing AI

These links ground the setup facts. They do not substitute for inspecting the installed game or constitute proof that the proposed mod works.

1. [MSCLoader repository and installation](https://github.com/piotrulos/MSCModLoader)
2. [MSCLoader releases](https://github.com/piotrulos/MSCModLoader/releases)
3. [Visual Studio/.NET setup](https://github.com/piotrulos/MSCModLoader/wiki/Install-Visual-Studio-for-MSCLoader)
4. [Template installation](https://github.com/piotrulos/MSCModLoader/wiki/Install-template-for-Visual-Studio)
5. [Using the templates](https://github.com/piotrulos/MSCModLoader/wiki/How-to-use-templates)
6. [MSCLoader API and PlayMaker guidance](https://github.com/piotrulos/MSCModLoader/wiki/MSCLoader-API)
7. [Character/other asset-bundle workflow](https://github.com/piotrulos/MSCModLoader/wiki/How-to-create-and-use-Asset-Bundles)
8. [Current MSCLoader debugging workflow](https://github.com/piotrulos/MSCModLoader/wiki/Debugging-your-mods-in-Visual-Studio)
9. [dnSpyEx](https://github.com/dnSpyEx/dnSpy)
10. [MSCToolset author's page](https://www.nexusmods.com/mysummercar/mods/710)
11. [LiteNetLib current requirements and legacy branch pointer](https://github.com/RevenantX/LiteNetLib)
12. [Tailscale Windows setup](https://tailscale.com/docs/install/windows)
13. [Tailscale direct and relayed connections](https://tailscale.com/docs/reference/connection-types)
14. [MSCEditor author's FAQ, including save location](https://www.nexusmods.com/mysummercar/mods/8?BH=0&tab=docs)

Compatibility note: the MSCLoader API distinguishes My Summer Car and My Winter Car. This specification targets My Summer Car. Recheck linked documentation and local runtime evidence when implementation begins.
