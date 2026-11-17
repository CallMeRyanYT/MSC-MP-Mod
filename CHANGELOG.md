# Changelog

## 0.10.0 - 2026-11-17

**Both players need 0.10.0** (the network protocol changed).

- **Friends' progress is saved with the world.** Everybody's needs (hunger, thirst, tiredness, stress, toilet, dirt, drunkenness) are shared every few seconds. When the host saves at the toilet, every friend's last place and needs are written next to the host's save (`friends_progress.txt` in the mod's settings folder). When a friend loads into that world again, they're put back where they were with the same needs. Starting a new game forgets them.
- **Rejoin after a crash**: a friend who reconnects is put back where they were, even without a save in between.
- **Respawn after death**: a friend who dies comes back into the host's world by themselves after a few seconds, starting fresh, instead of having to click Rejoin.
- **Find each other**: markers with name and distance for friends who are far away or behind you (can be switched off in the settings). **Ping** (middle mouse button) shows everyone where you're looking for 10 seconds. **Go to** in the F2 panel teleports you to a player.
- **Emotes**: an emote menu (**B**, rebindable) with wave, point, cheer, shrug, drink, facepalm, sit down and dance. Pick with the number keys or the mouse. Copying bots repeat your emotes.
- **See how your friends are doing**: drunk players sway, sleeping players lie in bed, and the player list says who is drunk, hungry, thirsty, tired or needs the toilet.
- **Host controls**: per player *may drive* and *shared money* (untick to give them their own wallet), **Kick**, and **Lock the game** so nobody new can join (players from this session can still come back).
- **Loopback test**: *Test: see yourself and your bots as friends see you* in the F2 panel draws a copy of you and your bots next to you, sent through the real network format. The self-test also checks these copies and every emote.

## 0.9.3 - 2026-11-17

Works together with 0.9.2 (same network protocol), but both players should update for the fixes.

- Fixed: for players who joined, items in their hand bumped into walls and engine blocks. Held items now pass through things for everyone, like in single player. The game handles what you hold, and the mod no longer switches its physics back on.
- Fixed: running players and bots floated about 0.8 m above the ground (the run animation is borrowed from a child's body and lifted the whole skeleton). The lower foot is now kept on the ground.
- Fixed: crouching players hovered about 12 cm above the ground.
- Better crouching: feet stay planted while the hips go back, the back leans in for balance and the arms come forward.
- Better looking around: looking up and down now moves the neck and upper back too, not just the head, and the head leads into turns.
- Leaning shows: the back bends sideways or forward when a player's view moves off-centre from their feet.
- Less delay: players and cars update 30 times a second (was 20) and are shown 85 ms / 70 ms behind (was 120 ms / 100 ms). Items update 20 times a second (was 12), 75 ms behind (was 100 ms).
- The MSCLoader console stays closed while you play. Warnings and errors only go to `mscmp.log`, and appear in the console only with **Detailed log** switched on. The bot body check no longer floods it.
- Bots: **Tidy up** only moves loose items (shop goods, tools, bottles), never car parts you laid out. A job shared by several bots ends with one "Done" message instead of one per bot.

## 0.9.2 - 2026-11-17

- Fixed: the panel could say "Steam is not running" for the whole session even with Steam open. The mod checked Steam only once, at start-up. Now it starts Steam's connection itself if the game hasn't, keeps retrying every few seconds, has a **Try again now** button, and says exactly what's wrong (Steam closed, game not started through Steam, nobody logged in).
- Fixed: `Install.bat` now removes renamed extra copies of the mod (like `MSCMP (1).dll`), which would load twice.
- New: **everyone sees your bots**, with your name in their name tags. Their walking, carrying, waving and reaching are sent like a player's.
- New: **bot jobs**. Fetch the item you're looking at, collect every item of a kind into a pile (bottles, firewood...), tidy up around you, fetch anything nearby, or take an item to home, Teimo's or Fleetari's. Several bots share a job.
- New: following bots ride along in your passenger seat when you drive, and get out with you.
- New: **see your own character.** *Your character* in the F2 panel shows a turning preview with arrows to change body, shirt, trousers and face. Changing them in MSCLoader's settings pops up the preview for a few seconds (in the world).
- **Both players need 0.9.2.** The network protocol changed again.

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
