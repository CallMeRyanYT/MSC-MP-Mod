# Shared item catalogue

Version 0.4.0. **Pickup, holding, drop, throw and rigidbody motion only. Actual multiplayer game behavior is unverified.**

The installed Steam build 20171487 contains the `SaveItems` PlayMaker FSM (serialized component 113413). Its 21 owner-independent save/load pairs use persistent tags and explicit FsmGameObject references. The adapter selects the following 17 pairs. It verifies the initialized save/load actions resolve to the same target and `defaultES2File.txt`, and refuses missing or ambiguous contracts. Runtime names, paths, instance IDs, coordinates and discovery order are not identity.

| Item | Registry reference | Persistent transform tag | Static object ID |
| --- | --- | --- | --- |
| Axe | Axe | AxTransform | 3485 |
| Second axe | Axe2 | Ax2Transform | 32896 |
| Gasoline can | Gasoline | JerryCanGasTransform | 8657 |
| Diesel can | Diesel | JerryCanDieselTransform | 13285 |
| Sledgehammer | Sledgehammer | SledgehammerTransform | 32042 |
| Spanner set | Toolbox | ToolboxTransform | 33470 |
| Dipper | SaunaDipper | SaunaDipperTransform | 9400 |
| Second dipper | SaunaDipper2 | SaunaDipper2Transform | 33424 |
| Water bucket | SaunaBasket | SaunaBasketTransform | 21074 |
| Second water bucket | SaunaBasket2 | SaunaBasket2Transform | 11186 |
| Sofa | Sofa | SofaTransform | 15572 |
| Lantern | Lantern | LanternTransform | 5339 |
| Camera | Camera | CameraTransform | 11224 |
| Wiring mess | WiringMess | WiringMessTransform | 29115 |
| Garbage barrel | GarbageBarrel | GarbageBarrelTransform | 7199 |
| Digging bar | DiggingBar | DiggingBarTransform | 23717 |
| Notepad | Notepad | NotepadTransform | 35454 |

Five further objects carry their own persistent owner-bound contracts:

| Item | Persistent tag | Static FSM | FSM name / states | Required idle state |
| --- | --- | --- | --- | --- |
| Coffee cup | CoffeeCupPos | 110846 | Use / 14 | Wait player, Wait button or Delay |
| Grill | GrillPos | 106348 | Data / 5 | State 1 |
| Coffee pan | CoffeePanPos | 107563 | Data / 8 | Idle or State 2 |
| Flashlight | FlashlightTransform | 113629 | Use / 12 | Wait player or Wait button |
| Portable radio | PortableRadioTransform | 113921 | Data / 6 | Idle |

Each object must be active, free, on pickup layer 19, tagged PART or ITEM, with exactly one root rigidbody, finite positive mass no greater than 200, no joints and bounded collider/FSM counts. Static scene inspection confirms one body for the 22 selected targets. The cup/grill/pan/flashlight/radio have respectively 1/7/4/4/7 colliders and no joints. The floor jack and motor hoist have multiple bodies and mass 9999 and are excluded. The car jack, fish trap and dynamic consumables are also excluded pending their own interaction/factory adapters.

The `SaveItems` registry must have completed loading into `State 1`. Discovery finishes before mutation. Shared startup requires the existing disposable-profile guard. Each body receives one host ID; the client's baseline must match the exact 22-item catalogue plus van. Capture saves the original parent, layer, kinematic state, collider enabled flags and FSM enabled flags in memory. Shared mode disables native pickup, item use FSMs, trigger colliders and the registry's ordinary save/load FSM. Host bodies simulate; client bodies are kinematic. Holding disables all body colliders, and release restores only the captured solid colliders. Teardown restores captured flags. No profile, backup or save file is read or written by this adapter.

Special functions remain gated: no pouring/drinking, refuelling, cooking, lights/radio controls, tool use, wiring, sleep, fire or garbage destruction. Child visuals are not semantic state replication. Their appearance may differ with different initial disposable profiles. External world progression is not made authoritative by this catalogue. Do not use this experimental build for a persistent cooperative playthrough.

Motion is sampled at 10 Hz. At most eight changed item records are published per sample with a rotating cursor, plus the van. Quiet bodies produce no motion record. Under a whole-catalogue motion burst, all 22 are visited within three samples. World transport batches up to 16 ordered commits every 50 ms and retains pending cursors under backpressure; it does not drop ownership transitions. No measured Unity performance or latency claim is made.

Evidence comes from the installed level2 scene and initialized action types in Assembly-CSharp, inspected read-only with `tools/InspectGame/inspect_scene.py`. Private scene dumps and game assemblies remain ignored and are not redistributed. Static evidence proves authored contracts, not actual hook timing, rendering or physics behavior.
