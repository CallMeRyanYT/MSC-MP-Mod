# Changelog

## 0.9.1 - 2026-11-17

- Fixed: other players' bodies stretched up into the sky. Crouching moved the hips without the legs and kept adding up every frame. Standing players were also wrongly read as slightly crouched.
- Fixed: bone adjustments (head look, crouch, steering) could pile up frame after frame on bones no animation drives.
- Fixed: the mouse sometimes stayed unlocked after closing the F2 panel or chat. The mod now gives the mouse back to the game reliably and restores exactly what it changed. Esc closes the panel.
- Fixed: items picked up by a friend could pass through walls. The host wrongly kept thinking it still held the last thing it dropped, so it refused the friend's pickup and the item froze in their hand. Held items now always keep normal game physics and collisions. They switch to following the other player only after being dropped.
- New: test bots for testing alone. Spawn as many as you like from the F2 panel and give them orders: copy me, follow, stay, wander, go to home / Teimo's shop / Fleetari's (walking or teleporting), fetch the nearest item, wave or change body. Only you see them. Each bot writes its body measurements to the log.
- New: liquids and other world values are shared: fuel, oil, coolant, brake and clutch fluid, water, battery charge, wear, damage, dirt and paint colour. Whoever drives or holds something decides its values. Anyone's pouring still counts.
- New animations: small steps when turning on the spot, tucked legs while jumping or falling, and players reach towards the doors, switches, bolts and parts they use.
- Fixed: values that rise by themselves (engine temperature, battery charge, wear, dirt) climbed several times too fast in multiplayer, because every player's copy reported its own rise as "poured in". Only liquids (fuel, oil, coolant, fluids, water, drinks) can now be topped up by someone else, and only when they stand next to it.
- Fixed: other players briefly shuffled their feet when they appeared or got out of a car.
- Fixed: grabbing an item out of a test bot's hands made the two of you pull it back and forth.
- Fixed: the build script's API audit could fail with "The parameter is incorrect" on some machines, and the release zip was named `MSCMP-0.9.1.0.zip`.
- **Both players need 0.9.1.** The network protocol changed, so 0.9.0 and 0.9.1 can't play together (you'll get a clear version-mismatch message).

## 0.9.0 - 2026-11-17

A complete rewrite. Nothing of the old networking code is left.

- **Connecting:** Steam lobbies, invites and P2P instead of direct IP connections, so there are no addresses, ports or VPNs to set up. Version and game-build checks happen when you join.
- **Shared world:** friends get an exact copy of the host's save, plus everything that happened since the host loaded (world log). Their own save is backed up and restored automatically, including after a crash. The host's save is never touched.
- **Player models:** rebuilt from the game's own characters, clothes and animations, with walking, running, crouching, head look, driving and passenger poses and waving. Body and clothes can be chosen in the settings.
- **Items and parts:** pick up, carry, drop and throw with host-arbitrated ownership. Spawned objects are linked between games.
- **Vehicles:** every car, the boat and the trailer. The driver simulates the car; replicas get the same inputs with physics correction. Passenger seats.
- **World interactions:** state-machine mirroring for doors, switches, appliances, the dashboard, the ignition, assembling and removing parts, bolts and wiring, with player-only effects filtered out.
- **Time, weather and money** follow the host; the wallet is shared.
- **Interface:** F2 panel, chat, player list, name tags, notifications.
- **Install:** one-click `Install.bat` that finds the game in any Steam library, checks MSCLoader, removes old builds and installs. Also `Uninstall.bat`.
- **Build safety:** an API audit checks every game API the mod uses against the real game assemblies.

## 0.5.0 and older

Experimental builds made with an AI coding agent (Codex), using direct LiteNetLib connections. They were replaced by 0.9.0 because players couldn't connect.
