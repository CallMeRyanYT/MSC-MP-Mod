# Coffee-cup binding and pickup boundary investigation

This page preserves the original read-only cup investigation. Version 0.4.0 adds a separate mutating adapter documented in [SHARED_ITEM_CATALOGUE.md](SHARED_ITEM_CATALOGUE.md).

The investigation below is static evidence and a compiled **read-only diagnostic binding**. It is not a working pickup interceptor, a shared-item implementation, or an in-game verification result. No game state, FSM action, event, personal profile, or save file was changed during this investigation.

## Evidence and important correction

The inspected installation is Steam build 20171487 / Unity 5.0.0f4. Its authored GAME scene is `mysummercar_Data/level2`, SHA-256 `39e5c8f38edd83652325b403e06449eb6fe4e5c588e4dad2bf04b5c9ff406c31`. The existing scene inspector reconstructed the selected FSMs from their exact serialized bytes. Additional full action parameter tables and managed action implementations were inspected locally; extracted/decompiled material remains ignored under `.local/inspection` and is not a distributable dependency.

**The coffee cup becomes a `PART`, not an `ITEM`.** Its `Use` FSM `Data` state action 6 sets the owner's tag to `PART`. It therefore reaches `Set pivot` and `Part picked`. The `Set pivot 2` / `Item picked` route discussed for generic ITEM objects is relevant to objects such as the flashlight, not the coffee cup. This distinction was established by inspecting the complete action fields, not by assuming behavior from the object's name.

Serialized IDs below locate authored evidence only. They are never used as network identities or runtime lookup keys.

## Persistent identity and runtime resolver

The cup's authored owner is GameObject 23956, with `Use` FSM component 110846 and 14 states. Its persistence variables are:

| Variable | Authored value |
| --- | --- |
| `UniqueTagTransform` | `CoffeeCupPos` |
| `UniqueTagCoffee` | `CoffeeCupCoffee` |
| `UniqueTagCaffeine` | `CoffeeCupCaffeine` |

`Save game` has `SaveFloat`, `SaveFloat`, `SaveTransform`; `Load game` has `LoadFloat`, `LoadFloat`, `LoadTransform`, `SetScale`. The transform actions operate on their owner, reference `UniqueTagTransform`, and use the literal relative file `defaultES2File.txt`. `Check data/Exists` uses the same tag/file. The load action does not request Resources storage. These fields establish the candidate authored persistence role.

`SupportedItemBinding` performs a bounded, explicit discovery pass over loaded PlayMaker components. It requires exactly one `CoffeeCupPos` string claim, including inactive loaded instances in ambiguity detection. It then validates the owner's initialized FSM state/action/type/enable layout, save/load contract, content variables, and PART-tag action. It also requires one active `PickUp` FSM matching the inspected 37-state contract, one cup Rigidbody and BoxCollider, and one FixedJoint on the hand.

The key is `msc-20171487:defaultES2File.txt:CoffeeCupPos`; the diagnostic archetype is `msc.coffee-cup`. No object name, hierarchy path, current position, runtime instance ID, or discovery order is used to select the cup. A copied object claiming the same persistent role causes discovery to fail. Including inactive loaded assets can produce a conservative false ambiguity; the resolver reports it instead of choosing an instance. Cross-process mapping remains unverified.

The binding stores actual component/variable references after discovery, so reparenting does not change identity. It reads initialized actions only after checking `IsInitialized` and `ActionsLoaded`. This matters because the installed `FsmState.Actions` getter can otherwise load actions lazily. It does not call that initialization path, `PlayMakerFSM.Fsm`, action `OnEnter`, or an event/state-changing API.

The cup's authored Rigidbody has mass 0.8, drag/angular drag 0.5, gravity enabled, non-kinematic state, and continuous collision detection. Its root has a non-trigger BoxCollider sized approximately 0.075 × 0.075 × 0.1. These values are evidence and diagnostics, not an instruction to overwrite runtime physics.

## Earliest candidate request boundary

The active authored `PickUp` FSM is component 113435. Another same-named FSM on the inactive old hand makes an unrestricted name-only lookup ambiguous. The observed route for the cup is:

`Look for object` → `State 1` → `Check moped?` → `Check if Item` → `Check if Part` → `Is Jonnez` → `Set pivot` → `Part picked`.

The first two stages do the following:

1. `Look for object` maintains the normal selection state, sets `HandEmpty=true`, and updates `RaycastHitObject` with a one-metre MousePick ray using layers 19, 15, and 9. Left click emits `FINISHED` toward `State 1`.
2. `State 1` action 0 **deactivates** the referenced `SelectItem` object (GameObject 29909). It does not activate or deactivate the picked item. Action 1 assigns `PickedObject=RaycastHitObject`.

The smallest promising authority gate is a scoped routing state between `Look for object`'s `FINISHED` transition and `State 1`. It can freeze a supported candidate and await its correlated authority result before the selector or picked reference changes. An accepted local-host request would continue into the approved path; a denial would return to the normal look state. This is a proposed integration, not installed code.

The installed PlayMaker implementation calls each action's `OnEnter` and checks `Fsm.IsSwitchingState` before proceeding to the next action. That supports investigating a scoped transition guard, but real execution order and teardown still require an actual game test. Simply inserting an unfinished action is insufficient: these authored states are not action sequences, so later actions would still execute unless a state transition is actually pending. Prefer an isolated routing state over an assumed asynchronous pause inside an existing state.

## Exact pickup and release effects

For a cup following the normal PART branch:

| State | Observed effects in order |
| --- | --- |
| `Set pivot` | `HandEmpty=false`; read cup world position into `ObjPos`; move the hand and ItemPivot to that world position; inspect picked name for the unrelated motor-block special case |
| `Part picked` | Clear `PlayerVelocity`; cup layer becomes 16; parent cup to ItemPivot without resetting local position/rotation; connect the hand owner's joint to the cup Rigidbody; poll drop/throw input |
| `Drop part` / `Drop part 2` | **Set the unrelated SATSUMA Rigidbody non-kinematic first**; set picked item non-kinematic; detach its parent; set layer 19; disconnect the hand joint; assign world velocity from `PlayerVelocity` |
| `Throw part` | Read picked mass; calculate `(mass + 1) * 2`; normalize `PlayerDir`; multiply by `PlayerStrenght * ((mass + 1) * 2)`; add force in world space using `ForceMode.Force` |
| `Wait` | **Activate unrelated JONNEZ collider object first**; reset hand local position to `(0,0,0.6)`; reset picked scale to one; clear PickedObject; wait 0.1 seconds in real time before joint checking and return to look |

The unrelated references are SATSUMA GameObject 28145 and `JONNEZ ES(Clone)/Colliders/Coll` GameObject 26140. Their names/paths identify evidence only. **Do not drive the entire native Drop or Wait state for a supported cup release**, because doing so also mutates vehicle state. A later scoped adapter must preserve the necessary hand/item cleanup while deliberately excluding those unrelated effects.

The native throw formula yields a force, not a velocity. Its physics timing and resulting velocity must be measured before translating it into host-authorized throw behavior. The existing pure authority core's configurable velocity limits do not establish parity with this native force action.

## Unsupported parallel mutations

The cup's separate `Use` FSM can pour or drink through `Wait button` transitions `POUR → Cup full?` and `USE → Check drink`. Save/load and content behavior share that FSM. Disabling it globally would also disable necessary persistence behavior and is not a valid integration strategy. A limited shared pickup slice needs scoped use/consumption gating or a supported transaction adapter before it can be enabled.

The hand also has `Rotate object` and `LeftALT` FSMs. The rotation FSM can change held-object rotation. Those inputs and their authority must be inspected and gated or replicated separately. Quaternion/angular-motion state is not supplied by the current position-only domain records. A moving/held cup visual alone would not prove a complete item adapter.

## Safe diagnostic integration

Call these APIs on Unity's main thread:

- `SupportedItemBinding.TryCreate(sceneGeneration, out binding, out reason)` once after GAME and its FSMs initialize, or from an explicit diagnostic button. It can report uninitialized actions or ambiguity without mutating anything. Do not run its whole-scene discovery every frame.
- `binding.TryObserve(sceneGeneration, out text)` samples retained references. It reports the item identity, active use/pickup states, aimed/picked relationships, hand flag, pivot parent, joint, layer, PART tag, collider/kinematic flags, mass, and live content values. A stable held relation requires the hand, joint, parent, and layer to agree; a stable free relation requires all held references to be absent.
- `binding.TryGetPickupBoundaryCandidate(sceneGeneration, out candidate, out reason)` revalidates the contract and reports whether the native pre-click look state currently aims at this free cup with an empty hand. It rejects loading/saving/pouring/drinking states, disabled/trigger colliders, kinematic or wrongly layered/tagged objects, existing hand/joint references, and moped pickup behavior. This is a diagnostic eligibility result, never an authority grant or a substitute for host distance validation.
- `binding.Dispose()` on scene teardown releases references; there are no component settings or hooks to restore.

The candidate accepts normal cup Use states `Wait player`, `Wait button`, or `Delay`. It also guards the game's special `block(Clone)` branch if another mod renames the cup to that value; this name comparison protects native branching and is not used for entity identity.

The diagnostic can be displayed beside the existing inspector or written to the bounded trace. Its next useful runtime proof is: discover one cup; aim without clicking; capture an eligible candidate; perform an ordinary vanilla pickup/drop in the disposable test profile while observing the held/free relationships; re-enter the scene and confirm stale bindings refuse sampling. Only after those observations should a routing/denial experiment be added, followed by proof that a denied pickup leaves cup parent/layer/physics, hand references, selector state, and unrelated vehicles unchanged.

## Validation status

The binding compiles against the installed Unity 5, PlayMaker, and legacy .NET 3.5 references with warnings treated as errors. Its contract was cross-checked against the complete authored action parameters, including the intentionally disabled `SetStringValue` at cup `Wait button` action index 2. No Unity/native methods were invoked in the standalone check. No runtime discovery, interception, two-game mapping, or shared pickup acceptance test is claimed.
