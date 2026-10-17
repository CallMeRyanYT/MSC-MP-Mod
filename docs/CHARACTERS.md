# Original character presentation

Two appearance options are implemented in source: **Male** (`1`) and **Female** (`2`). These are visual choices, not permissions or gameplay classes. The renderer accepts only those values and requires a validated appearance baseline before displaying a remote player. A live appearance change removes the old renderer before accepting the replacement baseline.

The geometry, texture pixels and simple animation code are original work authored for this repository with AI assistance. No My Summer Car NPC mesh, texture, animation, proprietary character, downloaded model or other mod's assets are included. The intended visual direction is ordinary work clothing in the early 1990s Finnish countryside, consistent with the setting described by the [official My Summer Car site](https://amistech.com/msc/). The result still needs visual acceptance inside the actual game; this is not a claim to match every aspect of its art style.

`CharacterMesh.cs` creates a faceted head, shaped torso, articulated arms/hands and legs/workshoes. Male uses muted green-grey checked clothing and shorter hair; Female uses muted rust checked clothing and longer hair. Both have denim, a belt, a cream undershirt, small original face details and restrained proportions. The renderer does not use Unity capsule/sphere placeholders. Each character has nine rigid mesh parts, one Diffuse material and one original 256 x 128 RGB texture atlas. Meshes, material and texture are owned and explicitly disposed; no asset-bundle or Unity Editor install is needed to reproduce these source-authored assets.

Walking is a modest procedural arm/leg swing driven by accepted movement speed, with a subtle idle motion. Shared mode adds a raised carrying arm and bent seated legs/arms for the authoritative driver, for both appearance options. Crouching, death and a full skeletal animation set remain unfinished. Avatars are noninteractive presentation with no colliders, rigidbodies, camera, audio listener, input handler or save component. Runtime appearance and alignment still require game validation.

`CharacterPreview` exposes `Texture GetPreview(CharacterStyle)` for the menu's two choices. It owns a disabled camera that renders only on demand into a separate 256 x 384 texture per choice. Its distant stage and short-range, layer-masked lights are inactive outside that render. It restores the prior active render target afterward, rebuilds a lost render texture after a device/fullscreen transition, and disposes owned resources with the panel. It never modifies the game's camera or ambient lighting. Rendering is called only on the Unity main thread during the GUI repaint pass.

Source geometry reaches approximately 1.827 m above the feet. The portrait framing uses an orthographic half-height of 1.03 m and an explicit 2:3 aspect ratio. A conservative neutral-pose bounding box fits within that frame with head/foot margins. This is a geometric check, not a screenshot or in-game rendering result.

## Runtime acceptance still required

1. Open the panel at the main menu without starting a network session. Select Male, then Female. Capture each portrait and verify the face, hair, shirt, hands, jeans and shoes appear, with no clipping, black texture or leftover previous selection.
2. Leave the menu open, switch choices repeatedly, and close/reopen the panel. There should be no repeated per-frame resource creation, repeated error log or visible lighting change in the game scene.
3. Change resolution/fullscreen mode, then inspect both portraits again. A lost render target should be regenerated, and the main game/GUI rendering must remain intact.
4. In two disposable, isolated game sessions with accepted appearance baselines, observe the remote character from the front, side and back; walk, stop, turn and change appearance. Confirm exactly one remote character per identity, no raycast/physics interference, and clean removal on disconnect.
5. Re-enter a scene and disable/re-enable the mod through the supported loader flow. Verify cameras, portrait textures, mesh resources and remote renderers are released/recreated once. Record the game/mod fingerprints, screenshots, diagnostic run IDs and actual observations.

Compilation and relay tests do not prove rendering. No runtime acceptance is recorded by this document; append actual evidence to the test record only after those observations are made.
