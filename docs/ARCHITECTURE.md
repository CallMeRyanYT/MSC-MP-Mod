# How MSC Multiplayer works

A tour of `src/MSCMP` for developers. The mod is one assembly (`MSCMP.dll`, .NET 3.5, C# 7.3) loaded by MSCLoader.

## The big picture

```
Steam lobby + P2P ──► Session (players, handshake, routing)
                          │
     ┌────────────┬───────┼─────────────┬──────────────┬─────────────┐
 SaveSync     GameSession  PlayerSync   ObjectSync    VehicleSync   FsmSync / WorldSync
 (host save   (menu→load→  (avatars,    (items/parts, (cars, seats) (world events,
  to clients)  catch-up)    20 Hz)       ownership)                  clock/weather/money)
```

- **One world, the host's.** Clients receive a byte-exact copy of the host's save, load it with the normal *Continue* button, and then apply everything that happened since the host loaded.
- **The host orders everything.** Every world event goes through the host, which relays it and appends it to the **world log**. A late joiner gets: the host's save + the world log + the current position of every object. That's an exact copy, because My Summer Car only writes its save when you quit.
- **Whoever touches it simulates it.** The player holding an item, or driving a car, runs its physics locally (so it feels like single player). Everybody else gets a replica that follows. The host grants and revokes ownership, so two players can't hold the same thing.

## Folders

| Folder | What's inside |
| --- | --- |
| `Core/` | `MP` (lifecycle: menu, load, world ready/left, per-frame dispatch), `Log`, `Options` (MSCLoader settings and keybinds), `GameUtil` (paths, PlayMaker helpers) |
| `Net/` | `SteamNet` (Steamworks lobbies, invites, P2P channels), `Session` (handshake, players, routing, timeouts), `NetBuffer` (binary writer/reader), `Msg` (message ids), `BatchSender` |
| `Saves/` | `SaveSync`: host save snapshot, chunked transfer with CRC, client backup journal and restore, crash recovery |
| `Game/` | `GameSession` (join flow and catch-up), `MenuControl` (presses *Continue* for clients), `LocalPlayer` (reads the local player, passenger mode), `PlayerSnapshot` |
| `Avatars/` | `Appearance` + `Catalog` (bodies and clothes), `AvatarFactory` (rebuilds a game NPC's skinned mesh, skeleton and animations), `RemoteAvatar` (animation, crouch, head look, driving pose) |
| `Sync/` | `PlayerSync`, `ObjectSync`, `VehicleSync`, `FsmSync`, `WorldSync`, `WorldLog`, `SnapshotBuffer` |
| `UI/` | `MultiplayerUI` (IMGUI panel, chat, toasts, name tags), `Notifications` |

## Networking

Steam P2P (`SteamNetworking`) with three channels: 0 reliable (events), 1 unreliable (20 Hz states), 2 reliable bulk (save transfer). Players find each other through a **friends-only Steam lobby**. The lobby carries the mod version, and invites use the overlay and rich presence (`+connect_lobby`).

MSCLoader destroys every GameObject when you return to the main menu, including the game's `SteamManager`, whose `OnDestroy` shuts Steam down. A Harmony prefix skips that, and the mod pumps `SteamAPI.RunCallbacks()` itself, so a session survives menu ↔ game transitions.

Loading the game freezes it for up to a minute. Timeouts allow for that, and real disconnects come from Steam's lobby and P2P callbacks.

## Players

`LocalPlayer` samples the local player 20 times a second: position, look direction, crouch, vehicle and seat, held object. `RemoteAvatar` interpolates ~120 ms behind using a clock offset measured with pings.

Avatars are **rebuilt from the game's own NPCs** (`AvatarFactory`): the `SkinnedMeshRenderer`'s shared mesh, the bone hierarchy (transforms only, no AI/FSMs/colliders) and the clothing materials, plus the game's legacy animation clips on layered `Animation` components (legs, breathing, each arm, seat). Crouching, head look and steering are bone rotations applied in `LateUpdate`.

## Objects (`ObjectSync`)

Every pickable rigidbody in the scene (tags `ITEM`/`PART`/`TOOL`, layers Parts/DynamicBodies) is registered at load with an id derived from its scene path (FNV-1a, duplicates ordered by sibling index), so all games agree without talking. Objects created later get host-assigned ids. Objects created by a synced world event get an id derived from that event, see below.

Picking something up claims ownership optimistically. The host confirms or refuses (someone else is holding it). Owners send changed poses about 12 times a second and a reliable rest pose when the object stops. Replicas are kinematic and follow.

## Vehicles (`VehicleSync`)

The driver owns the car. Every other game keeps the car physically simulated, feeds it the driver's exact inputs (steering, throttle, brake, clutch, gear), and steers its rigidbody towards the driver's pose with velocity corrections. So wheels, suspension and engine sound look right on every screen. Parked cars are kinematic replicas. The game's *ENTER DRIVING MODE* trigger is switched off while someone else drives. Passenger seats are assigned by the host. The passenger is parented to the car with the camera at the mirrored driver head position.

## World events (`FsmSync`)

My Summer Car runs nearly all interaction through PlayMaker state machines. At load the mod finds, in every scene FSM, the **states that player input leads to**: transitions fired by `GetMouseButtonDown`, `GetButtonDown`, `GetKeyDown`, `MousePickEvent` (click), `cInput` buttons, or scroll-wheel compares. Bolts are added explicitly, because the wrench sends their events. A small hook action is inserted at the start of each of those states. When a hooked state is entered **through that input transition, near the local player**, the event (FSM id, state index, origin, serial, bound objects) goes to the host, which logs and relays it. The other games force the same state with PlayMaker's own `SwitchState` and run the transitions that follow, so they play the same sounds and animations and write the same save data.

Details that make this safe:

- **Player-only effects are not replayed.** Actions in the replayed chain that write the local player's globals (`Player*`, `GUI*`: money, needs, the HUD) or act on the local player's body, camera or hand are disabled for the replay.
- **Same objects.** `GameObject` variables that point at shared objects are re-pointed at the receiver's copies (for example *which part is being assembled*).
- **Same spawns.** Objects created by `CreateObject` in a replayed chain get an id derived from the event, so a removed part is the same object everywhere. A fallback message creates it if a game's copy of the event didn't.
- **Busy machines wait.** Events for an FSM that is inactive or mid-sequence are queued until it is idle (5 s limit).
- Menus, the player's own FSMs, saving, sleeping, minigames, phones, shops' checkouts and NPC/job FSMs are excluded.

## Time, weather, money (`WorldSync`)

The host sends the sky block (`Color.Time`), the sun angle, the clock, the day, and the cloud system (type, cloud id, position) every 2 seconds. Clients snap to them, forcing the sky/clock/weather FSMs into the matching state when needed. Money is one wallet: clients send what they earned or spent, and the host applies it and broadcasts the total.

## Join flow (`GameSession`)

1. Client joins the lobby → `Hello` (protocol, mod version, game build, appearance) → `Welcome`.
2. Host sends its save snapshot (taken in the main menu before loading). Client backs up its own save and installs the host's.
3. When the host is in the world, the client presses *Continue* (`MenuControl`).
4. Client loaded → host sends a **catch-up** batch: object manifest, world log, current object poses, vehicles, time/weather/money. Audio is muted meanwhile.
5. Client is *Playing*.

A host who started hosting mid-game is fine: the world log records from the moment the world loads, session or not.
