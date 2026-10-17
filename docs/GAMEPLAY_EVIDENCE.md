# Installed gameplay evidence and visual-only adapter

Inspected on 2026-10-17 against Steam build 20171487, Unity 5.0.0f4 and the locally installed assemblies. This document records **static authored scene/code evidence**, not an observed multiplayer session. No game process was launched for this investigation and no personal save or PlayerPrefs value was changed.

## Reproducible evidence

`tools/InspectGame/inspect_scene.py` reads the owned installation, reconstructs MonoBehaviour type trees from its matching assemblies, and verifies exact byte consumption. Tooling dependencies are UnityPy 1.25.4 and TypeTreeGeneratorAPI 0.0.10; ILSpy command line 9.1.0.7988 was used for managed-code inspection. These are local investigation tools, not mod runtime dependencies. Decompiled game code and generated scene metadata remain ignored under `.local/inspection` and must not be packaged.

The authored GAME scene is `mysummercar_Data/level2`, SHA-256 `39e5c8f38edd83652325b403e06449eb6fe4e5c588e4dad2bf04b5c9ff406c31`. It contains 36,045 GameObjects, 2,104 rigidbodies, one CharacterController, 10,288 MonoBehaviours, and 6,601 PlayMaker FSMs. The inspection parsed 6,600 FSMs successfully. The remaining `ITEMS/car jack(itemx)/Lift/lift` FSM, serialized ID 113971, failed strict decoding and is excluded from binding evidence.

The type-tree generator needs two reader normalizations for this old format: aligned MonoBehaviour enable-byte storage and distinct array container nodes for primitive arrays. UnityPy's pure reader is used because its accelerated reader did not honor the normalized header. Successful full byte consumption is checked; malformed/unsupported components are reported instead of treated as decoded.

Serialized IDs and hierarchy paths below locate evidence in this exact asset. They are **not network entity identities**.

## Player and camera

| Observed object/component | Evidence | Adapter behavior |
| --- | --- | --- |
| Root `PLAYER` | GameObject serialized ID 12476; no parent; sole CharacterController ID 102033 | Require exactly one active matching root |
| `CharacterMotor` | Component 107678, `Assembly-UnityScript-firstpass` | Require the exact component assembly/name; read no controller internals |
| `FPSInputController` | Component 107675, same assembly | Require one enabled input component before sampling |
| Root `MouseLook` | Component 107679 | No modification |
| `PLAYER/Pivot/AnimPivot/Camera/FPSCamera` | MouseLook component 111302; this object has no Camera | Do not mistake this object for the camera |
| `PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera` | Actual enabled Camera component | Resolve every direct child uniquely and require it to equal the current `Camera.main` when sampling |

`PLAYER` is authored inactive. Gameplay startup must activate it, so menu/early-load binding failure is expected. The implementation does not activate it. The CharacterMotor/FPSInputController headers and attached types were decoded; their detailed serialized fields were not supported by the type-tree generator and are not relied upon.

`LocalPlayerBinding` samples root world position and yaw on the Unity main thread. It refuses inactive/disabled controllers, absent or ambiguous paths, nonfinite positions, coordinates outside ±100,000, a different camera, a different scene generation, and non-GAME scenes. It performs no input, movement, physics, FSM or save writes. Vehicle driving, crouch/body animation, death and authoritative corrections are outside this binding's verified scope.

`RemoteAvatars` creates original faceted character meshes for the explicit Male and Female choices; it never clones a game character. Objects use Ignore Raycast, have no colliders, and have no input/audio/FSM/save components. An appearance baseline must be registered before any pose can create a mesh. Each instance belongs to one GAME scene generation, supports at most three remote players, retains eight pose samples, interpolates with 100 ms delay, and hides poses stale for two seconds. Disposal removes owned GameObjects, meshes, textures and materials and is repeatable. Proportions and animation remain subject to in-game checking.

`AvatarPreview` samples at 10 Hz through the accepted diagnostic session. The host renders connection-bound client poses and relays them to the other accepted clients. Clients accept relay records only from the bound host connection. Relay records have an exact 42-byte pose, 26-byte removal or 27-byte appearance shape and an independent `MAV2` magic/version. Appearance values are exactly Male=1 or Female=2. Client announcements must match their accepted connection identity and generation; ordering uses the transport envelope sequence rather than a client-chosen body sequence. Poses are rejected until a valid appearance baseline arrives, including after reconnect. The host replays known appearance baselines to late joiners, and bounded one-second announcements recover missed subscriptions or enqueue failures. Live changes remove the old mesh before registering the new style. Appearance and pose sequence histories are independent. Actor ID and generation are checked; removal tombstones survive for the session and stale generations cannot resurrect an avatar. Historical records are capped at 128 and refuse new identities instead of evicting tombstones. The wrapper captures session GUID/generation and unsubscribes during disposal.

Handshake capabilities are explicitly 3: diagnostic networking (1) plus the required appearance-v2 contract (2). The outer transport remains version 2. Older capability-1 builds are rejected before session acceptance even when release labels match. The content manifest always includes the MSC-MP assembly's SHA256 independently of the loader's mod inventory. Failed removal enqueues enter a bounded retry outbox (at most three recipients and 128 identities per recipient); disconnect or recipient generation changes clear obsolete retries. This avoids a stale hidden avatar permanently occupying a remote-player slot after queue pressure.

The adapter and bridge compile against the installed Unity 5 references with the .NET 3.5 compiler and warnings as errors. The canonical runner passed 245 pure relay assertions, covering malformed/truncated packets, finite/bounded poses, duplicate/stale/wrapped sequences, removal/retry, reconnect, self rejection, the three-player limit, invalid appearance values, client identity spoofing, missing baselines, late-join appearance replay and live changes. These checks do not verify rendering, active camera selection, two-game pose exchange or gameplay movement consistency. This is an optional **visual pose preview**, not a shared-world multiplayer mode.

## Pickup flow

Two separate authored branches contain a `PickUp` FSM with the same 37 state names:

- `PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/HandOld`, component 107978: object authored inactive.
- `PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand`, component 113435: object authored active under the initially inactive PLAYER.

Therefore a whole-world search for FSM name `PickUp` is ambiguous. Runtime active hierarchy and exact expected action layout must be checked before a scoped injection is considered.

The latter FSM starts at `Look for object` and has variables `PickedObject`, `RaycastHitObject`, `ItemPivot`, `HandEmpty`, `Joint` and `ThrowStrenght` (the spelling is authored). Important inspected paths:

| State | Relevant ordered behavior |
| --- | --- |
| `Look for object` | `MousePick` stores `RaycastHitObject`; button/name checks select an object |
| `State 1` | `ActivateGameObject`, then `SetGameObject`; leads to moped/rack classification |
| `Check if Item` | `GameObjectCompareTag` checks `ITEM`; proceeds to `Set pivot 2`, otherwise part classification |
| `Set pivot 2` | Changes hand flag/positions, then changes picked object's layer, parent and connected rigidbody before `Item picked` |
| `Item picked` | Polls drop/throw/equip input; mutation has already occurred |
| `Part picked` | Changes layer, parent and joint connection before polling input |
| `Drop part` | Changes kinematic state, parent, layer, joint connection and velocity |
| `Drop part 2` | Performs the corresponding release changes, then enters `Throw part` |
| `Throw part` | Reads mass, computes force, changes joint connection and invokes `AddForce` |

An authority hook attached only to `Item picked` is too late: pickup has already changed the hierarchy/physics. Entry to `Set pivot 2` is a concrete investigation candidate, but earlier `State 1` also activates a referenced object; transition-only observation does not establish a safe cancellation point. No pickup/throw adapter or generic PlayMaker action patch has been enabled.

## Persistent identity candidates

The GAME scene contains **no authored ES2UniqueID, UniqueObjectManager or UniqueSaveManager components**, despite those types existing in `Assembly-CSharp.dll`. Their presence in metadata cannot establish usable world identity.

Authored item save tags are more concrete candidates:

| Item / FSM | Transform save tag | Additional state |
| --- | --- | --- |
| `ITEMS/coffee cup(itemx)` / `Use` | `defaultES2File.txt?tag=CoffeeCupPos` through `UniqueTagTransform` | Coffee and caffeine values |
| `ITEMS/flashlight(itemx)` / `Use` | `defaultES2File.txt?tag=FlashlightTransform` through `UniqueTagPos` | Battery count and charge; load sets ITEM tag |
| `ITEMS/radio(itemx)` / `Data` | `defaultES2File.txt?tag=PortableRadioTransform` through `UniqueTagPos` | Battery count and charge; load sets PART tag |

These are authored persistence roles, not proof of runtime uniqueness, duplicate-instance safety, spawn behavior or cross-process mapping. They need a resolver that verifies the owning FSM/variable/action contract and preserves a binding after reparenting. Using object name or path alone remains invalid.

## Persistence coverage

`mainData` PlayerSettings confirms company `Amistech` and product `My Summer Car`. Unity 5 documents Windows PlayerPrefs at `HKCU\Software\[company]\[product]`, establishing the exact key `HKCU\Software\Amistech\My Summer Car`. [Unity 5 PlayerPrefs documentation](https://docs.unity3d.com/500/Documentation/ScriptReference/PlayerPrefs.html)

Installed `ES2FilenameData.GetPersistentPath` uses `ES2GlobalSettings.defaultPCDataPath` when nonempty on Windows; otherwise it uses the cached `ES2FilenameData.persistentDataPath`, initialized from `Application.persistentDataPath`. The authored `Easy Save Default Settings` component has an empty PCDataPath. Relative paths therefore resolve to the game's persistent directory in this configuration; absolute identifiers can bypass that default.

The decoded scene contains the following authored save-action filename occurrences (not counts of active runtime writes): `defaultES2File.txt` 2,362; `items.txt` 165; `options.txt` 54; `trophies.txt` 12; `graveyard.txt` 10; and five `notepad.txt` references using a variable. Seven SAVEGAME button FSMs were located. The living-room button's `Mute audio` state already writes `GameVersion` before its later `Save all` state and `LoadLevel(MainMenu)` transition.

**Twelve ES2Auto components independently persist meshes to `meshsave.txt`.** All are authored with `saveOnDisable=true`, `saveOnInterval=false`, `isPlayMaker=false`, and `saveMesh=true`. Tags cover CARBODY, CARBODYMASSE, doors, bumpers, fenders, hood, bootlid and grilles. Inspected ES2Auto code saves on disable/quit. A toilet-save hook or loader OnSave callback alone cannot prevent these writes.

Other inspected writers are outside an individual world file:

- `cInput` stores input bindings, axes and calibration through PlayerPrefs.
- MasterAudio stores language and volume preferences through PlayerPrefs; generic PlayerPrefs FSM actions also exist.
- MSCLoader SaveLoad can write XML files under `Application.persistentDataPath`, while GF-mode settings, keybindings, console layout, update metadata and caches live under the game Mods/Config tree and Updates directories.
- The screenshot FSM action can write MyPictures when triggered.
- UnityCar setup code accepts a configurable path and can optionally prefix it with the persistent path. No CarSetup component was authored in GAME, but dynamic/mod-created instances have not been ruled out.

## Disposable test-profile tool

`scripts/test-profile.ps1` supplies Prepare, Status and Restore actions. It requires the game stopped and explicit evidence that this game's Steam Cloud is temporarily disabled. Preparation records all original file hashes and the exact PlayerPrefs key's value types in a flushed journal before moving the original directory to a unique sibling backup. The replacement directory is empty apart from a disposable marker. Restoration preserves the test directory as a separate archive, moves the original back, checks every original file hash, and restores only the known PlayerPrefs key. There is no broad registry import or deletion of save data.

Canonical path checks, reparse-point refusal, an exclusive journal lock, unresolved-journal refusal, wrong-target checks and interrupted-move recovery protect the procedure. Eighteen fixture assertions passed. Registry snapshot/restoration, Steam Cloud behavior, actual game save/quit and recovery after a real crash remain unverified. The tool was not run against the personal profile. It protects a single controlled diagnostic run; it is **not client save isolation** and must not be presented as such.

## Remaining runtime proof

1. A disposable game launch must prove loader startup, GAME activation and the exact camera binding, followed by exit/re-entry and teardown.
2. Two independently launched games must prove transport, host-to-client and client-to-client avatar relay, disconnect/reconnect and cleanup; then repeat with four players.
3. Record active pickup FSM state/action behavior and a safe pre-mutation request boundary before any item adapter is enabled.
4. Prove persistent tag uniqueness across independently launched worlds and reparenting, including repeated-looking items.
5. Enumerate and test all actual write paths before enabling world-mutating client gameplay.
