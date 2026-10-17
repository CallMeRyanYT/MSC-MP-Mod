# Game bindings and lifecycle evidence

Inspected target: Steam build **20171487**, Unity **5.0.0f4**, MSCLoader **1.4.2.410**, on **2026-10-17**. This ledger deliberately separates static evidence from actual in-game observation. No gameplay FSM transition, local player hierarchy, pickup interception point, or persistent entity ID has been verified in a running scene.

Additional authored scene/code evidence and the read-only walking-player adapter are in [GAMEPLAY_EVIDENCE.md](GAMEPLAY_EVIDENCE.md). Static evidence is now available for the actual player/camera hierarchy, active pickup FSM and several save writers; runtime validation remains pending. Implemented mutation adapters are documented in [SHARED_ITEM_CATALOGUE.md](SHARED_ITEM_CATALOGUE.md) and [VEHICLE_BINDING_EVIDENCE.md](VEHICLE_BINDING_EVIDENCE.md).

## Loader API verified locally

Source: `mysummercar_Data/Managed/MSCLoader.xml`, plus metadata/selected IL of the matching installed `MSCLoader.dll`. The current [maintainer API guide](https://github.com/piotrulos/MSCModLoader/wiki/MSCLoader-API) was checked as supporting documentation.

| Binding | Locally observed contract | Integration rule |
| --- | --- | --- |
| `Mod.ModSetup()` | XML restricts it to `SetupFunction` registrations | No scene lookup, filesystem setup, component creation or networking in this method |
| `Mod.SetupFunction(Mod.Setup, Action)` | Present; overload with status string also present | Register callbacks during setup; perform work in the proper callback |
| `Setup.OnMenuLoad` | Documented once in MainMenu | Create or observe diagnostics; do not assume this callback means a scene has unloaded |
| `Setup.PreLoad` | Phase 1, after GAME scene loads | Record entry; not proof all dynamically constructed objects exist |
| `Setup.OnLoad` | Phase 2, GAME fully loaded according to loader | Initialize scene diagnostics; gameplay bindings still require their own readiness checks |
| `Setup.PostLoad` | Phase 3, after mods finish phase 2 | Observe final loader phase without assuming all external mods are compatible |
| `Setup.OnNewGame` | Called for a new game rather than continuation | Diagnostic event only; must not replace personal saves to produce a test |
| `Setup.OnSave` | Documented after game saving | Observe only; cannot cancel or redirect the original save |
| `Setup.OnModEnabled` / `OnModDisabled` | Enabled/disabled through loader settings | Idempotent creation and cleanup must be tested; callback presence alone proves no teardown behavior |
| `Setup.Update`, `FixedUpdate`, `OnGUI` | Unity-style per-frame hooks | Keep runtime work bounded and on the Unity main thread |
| `ModLoader.CurrentScene` | Property; enum members `MainMenu`, `Game`, `NewGameIntro`, `Ending` | Useful diagnostic classification; `Application.loadedLevelName` supplies the actual scene name |
| `ModLoader.CurrentGame` | Property; `Game.MySummerCar`, `MyWinterCar`, combined enum value exist | Declare My Summer Car support only |
| `ModLoader.MSCLoader_Ver` | Version field exists | Log actual running value and assembly version during validation |
| `ModLoader.GetModSettingsFolder(Mod)` | Documented mod-owned settings path | Appropriate for diagnostic files; it is not a vanilla save-isolation API |

The complete installed `Mod.Setup` enum contains the entries above plus `ModSettings` and `ModSettingsLoaded`. It contains **no `OnUnload`, `OnSceneUnload`, or `OnApplicationQuit` registration**. Do not invent one. The loader itself has an `OnLevelWasLoaded` method and an internal `MSCUnloader`; these are implementation details, not public mod hooks.

The diagnostic runtime can use an owned Unity component to observe scene changes, focus/pause, destruction and application quit. Disposal must be safe when several exit signals arrive, when the mod is disabled, and when a scene is loaded again. Whether the loader recreates the mod instance or keeps an owned object alive in each path is **awaiting in-game evidence**. A successful compile does not settle those questions.

Unity's matching-version documentation provides the [OnDestroy](https://docs.unity3d.com/500/Documentation/ScriptReference/MonoBehaviour.OnDestroy.html) and [OnApplicationQuit](https://docs.unity3d.com/500/Documentation/ScriptReference/MonoBehaviour.OnApplicationQuit.html) contracts. An owned sentinel must have been active for `OnDestroy` to be available; the diagnostic component is created on an active object. These callbacks provide observation paths, not proof of a tested loader lifecycle.

## Installed scene and component evidence

| Observation | Source | What it does not prove |
| --- | --- | --- |
| `Assets/_Scenes/SplashScreen.unity`, `MainMenu.unity`, `Intro.unity`, `GAME.unity`, `Ending.unity` strings | Installed `mysummercar_Data/mainData` | Runtime scene ordering, build indices, or object readiness |
| Loader `OnLevelWasLoaded` compares `MainMenu`, `Intro`, `GAME`, `Ending` | Installed loader IL | That every loader callback runs on each reload or that cleanup succeeds |
| `CharacterMotor` type with `SetVelocity`, `SetControllable`, input fields, moving-platform state | `Assembly-UnityScript-firstpass.dll` metadata | Active player component, hierarchy, movement correction safety, or input ownership |
| `HutongGames.PlayMaker.Actions.WherePlayerIsLooking`, `SetHandsPosition` | `Assembly-CSharp.dll` metadata | Their attachment, variables, FSM states or usefulness for the chosen item slice |
| `PlayMakerFSM` plus `MSCLoader.PlayMakerExtensions` | Installed assemblies and XML | Any specific gameplay FSM, object name, state transition or injection position |

These are discovery leads. Do not use a class name or a string from serialized data as a verified game hook. Do not clone the player's local hierarchy: camera, input, save logic and audio ownership have not been audited.

## FSM integration policy

The installed loader exposes `GetPlayMaker`, `GetState`, `FsmInject`, `GetVariable<T>`, `GetEvent`, `AddTransition`, `GetAction<T>`, `InsertAction`, `RemoveAction` and `ReplaceAction`. XML describes scoped injection overloads for a `GameObject`, `Transform` or `PlayMakerFSM`, including a state name, action/delegate and insertion parameters.

The [maintainer API guidance](https://github.com/piotrulos/MSCModLoader/wiki/MSCLoader-API) prohibits Harmony patches of PlayMaker action classes. A future item adapter must use an inspected FSM instance and state, verify its expected action layout, retain exactly what it changes, and restore only its own changes during teardown. Version mismatch, missing state or ambiguous object must disable that adapter with an explicit diagnostic.

No FSM mutation is authorized by this evidence ledger alone. The diagnostic milestone inspects; it must not insert actions or change save/physics/input behavior merely because these APIs exist.

Even a property getter must be checked before claiming read-only inspection. Local IL inspection found that `PlayMakerFSM.get_Fsm` calls `Fsm.set_Owner`. The diagnostic inspector therefore uses the inspected `FsmStates`, `FsmName`, `ActiveStateName` and state `Name` getters rather than accessing `machine.Fsm`. Its camera-centre query is limited to 6 metres, at most 6 collider ancestors, 8 FSMs and 16 state names per FSM. This is a bounded discovery aid, not a complete world scan or an entity resolver.

## Persistence investigation ledger

| Candidate entry point | Static evidence | Remaining proof |
| --- | --- | --- |
| `ES2.Save`, `ES2Writer.Save`, `ES2File` methods | Types/methods in installed `ES2.dll` | All active call sites, identifiers, settings and destination paths |
| `UniqueSaveManager.OnApplicationQuit`, `Save`, `SaveObject`, `LoadObject` | Type/methods in `Assembly-CSharp.dll` | Whether any instance is active and what it persists |
| PlayMaker `SaveInt/Float/Bool/String/Vector3/Transform` and array-list/hash-table save actions | Action classes in `Assembly-CSharp.dll` | Actual attached states and transitions that invoke them |
| `MSCLoader.SaveLoad.SerializeSaveFile`, `WriteValue`, `SaveGameObject` | Loader metadata | When loader state is flushed, location, and interactions with vanilla saves |
| `ModLoader.SaveMods` | Installed IL invokes registered OnSave delegates and skips disabled mods | Ordering against every vanilla save path; it is not a pre-save interception point |
| `defaultES2File.txt` references | Strings in installed `level0` and existing per-user file | Full set of files, tags, client-isolation strategy and crash recovery |
| Existing `items.txt`, `meshsave.txt`, `graveyard.txt`, `trophies.txt`, `options.txt`, `Mods.txt` | Per-user directory listing only | Which files are world/player/settings data and all writers |

Do not patch these generic save actions to claim isolation. Trace active scene/FSM writes, loader/dependency writes and quit/transition behavior. A dedicated isolated test profile avoids risking the personal profile during discovery but is not an implementation of multiplayer client save isolation.

## Required evidence before gameplay adapters

1. Capture one bounded post-load dump in a disposable test profile. Include scene, object path for diagnostics, component types, FSM names, states, action types and relevant variable **names/types**. Avoid exporting personal save values.
2. Identify the actual player root, camera and controller by observed behavior. Record controller changes on entering a vehicle, death and reload before those combinations are supported.
3. Select one concrete item family. Trace pickup, hand attachment, release and throw through the actual FSM instances. Identify the earliest safe interception before world mutation and record cleanup behavior.
4. Establish cross-process identity from stable authored/persistent evidence. Runtime instance IDs and transform paths may locate diagnostics but cannot be network identities. Prove two same-named items remain distinct when discovery order or hierarchy changes.
5. Enumerate active save paths and ordinary save/quit transitions. Test isolated client operation and crash recovery before enabling world-mutating sessions.
6. Record the game build/content fingerprint, mod build, actual scene lifecycle, and evidence IDs for every newly verified binding. Fail closed when an expected binding is absent or ambiguous.

Scanning the world every frame is not an acceptable resolver. Discovery should be explicit and bounded; installed adapters should work from cached, validated references and dispose them at scene/session boundaries.

## Status of world bindings

| Area | Status | Activation gate |
| --- | --- | --- |
| Diagnostic loader lifecycle/API | Statically inspected; runtime test pending | Menu/game/reload/disable/quit evidence |
| Actual local player/controller | Unverified | Scene/component inspection and observed pose/correction behavior |
| Pickup/hold/drop/throw | Disabled | Inspected item family and atomic authority adapter |
| Persistent entity mapping | Planned | Independent-launch mapping and reparenting proof |
| Save isolation / save-generation publication | Unverified | Coverage of active writers and interruption tests |
| Vehicle seats/driving | Disabled | Vehicle-specific seat/input/physics inspection |
| Satsuma assembly/fasteners | Disabled | Part/slot/FSM/persistence inspection |
| Shops/economy/time/weather/NPCs | Disabled | Per-subsystem authority and binding evidence |

See [COMPATIBILITY.md](../COMPATIBILITY.md) for installation evidence and known runtime limits.
