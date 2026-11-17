# How to play

## Starting a game

**The host** (the player whose world you play in):

1. Start My Summer Car from Steam and stay in the **main menu**.
2. Press **F2** if the MSC Multiplayer panel isn't showing, then click **Host game**.
3. Click **Invite friends**. The Steam overlay opens. Invite your friends.
4. Press **Continue** when you're ready. Everybody else loads in automatically.

You can also click **Host game** while already playing. Friends who join get your world exactly as it is at that moment.

**Friends**:

1. Start My Summer Car from Steam and stay in the **main menu**.
2. Accept the invite: click the Steam chat notification, or open the Steam friends list, right-click the host and choose **Join Game**.
3. The panel shows *Getting the host's world...*. Your game downloads the host's save. A backup of your own save is made first.
4. When the host is in the world, your game presses Continue by itself and loads in. After a short *Syncing* moment you're in.

Up to 8 players can play. The host sets the limit in the mod settings (default 4).

## In the world

- **Picking things up** works as usual. While you hold something, nobody else can grab it out of your hands.
- **Driving**: get in as usual. While you drive, nobody else can enter the driver's seat. Everybody sees you drive, the wheels turn and the engine revs.
- **Passenger**: when a friend is driving, walk to the passenger door. You'll see *RIDE AS PASSENGER*. Press **Enter** (your driving-mode key) to get in, and **Enter** again to get out.
- **Building the car together**: assembling parts, removing them, tightening bolts with the wrench or ratchet and wiring are all shared. You can both work on the car at the same time.
- **Doors, switches, the TV, taps, the dashboard and the ignition** are shared.
- **Money** is one wallet for everybody: what anyone earns or spends changes everybody's money.
- **Time and weather** follow the host. If a friend sleeps, they wake up at the host's time.
- **Waving**: press **G**. **Emotes**: press **B** and pick one (number keys or click): wave, point, cheer, shrug, drink, facepalm, sit down, dance.
- **Finding each other**: friends who are far away or behind you get a marker with their name and distance. Press the **middle mouse button** to ping: everybody sees a marker where you're looking for 10 seconds. In the F2 panel, **Go to** next to a player teleports you to them (on foot only).
- **How friends are doing**: the player list (Tab / F2) shows if someone is drunk, hungry, thirsty, tired or needs the toilet. Drunk friends sway; sleeping friends lie in bed.
- **Chat**: press **T**, type, press **Enter**. **Esc** cancels.
- **Player list**: hold **Tab**. The F2 panel also shows everyone's status and ping.

## Music

Put your own `.ogg` songs in the game's `Radio` or `CD1`–`CD3` folders, as in single player. When you play one on the radio or the CD player, your friends hear the same song from the same speaker, at the same moment. If they don't have the song, their game fetches it from you through the host first (a few seconds), and keeps it in `music_cache` in the mod's settings folder so it only travels once. The cache cleans itself up above 500 MB.

Two switches in the mod settings: **Share my own songs** and **Play friends' songs**.

## Bots

Press **F2** in the world and click **Spawn bot**. Everyone in the game sees your bots (with your name in their name tag). Pick **All** or a bot's number, then give an order:

- **Copy me**, **Follow** (a following bot rides along in your passenger seat when you drive), **Stay**, **Wander**, **Go to** home, Teimo's shop or Fleetari's.
- Jobs. "That" means the item in the middle of your screen; the panel shows what you're looking at.
  - **Fetch that**: brings you that item.
  - **Collect all like that**: carries every item of that kind within 80 m (empty bottles, firewood, beer cases...) into a pile where you're standing. Give the order to several bots and they share the work.
  - **Tidy up here**: piles up the loose items within 15 m (shop goods, tools, bottles; never car parts).
  - **Fetch anything**: brings you the nearest thing it can carry.
  - **Take that to** home / Teimo's / Fleetari's: carries the item there and waits. Tick *Teleport* to skip the walk.
- **Wave**, **Next body**, **Remove**.

Bots can only move things nobody else is holding or using, and nothing heavier than about 60 kg.

## Saving

- **The host saves for everybody.** The host saves the normal way, with the toilet's *Save & Quit*. That saves the world and the host's progress, and also each friend's **last place and needs** (hunger, thirst, tiredness, stress, toilet, dirt, drunkenness). When a friend loads into that world again, they're put back where they were. Friends follow the host back to the menu, and back into the world when the host presses Continue again.
- If a friend's game **crashes**, they come back to where they were when they reconnect, even if the host hasn't saved since.
- If a friend uses the toilet to save, that only saves their temporary copy. It doesn't hurt anything; their game gets a fresh copy of the host's world next time.
- When a friend leaves (button **Leave** in the F2 panel, quitting the game, or the host stopping), **their own save is restored automatically**. If the game crashed, it's restored the next time they start the game.
- Every backup is also kept on disk (the last 8): `%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\MSCMP_backups`.

## Settings

Open MSCLoader's **Settings** in the main menu → **MSC Multiplayer**:

- **Body / Shirt / Pants / Face**: how you look to the others (all real game characters and clothes). In the world, a preview of your character pops up when you change these. You can also change them under **Your character** in the F2 panel, with a turning preview.
- **Max players** when hosting.
- **Name tags** above players.
- **Detailed log** (only needed when reporting a bug).
- **Show where far-away friends are** (markers).
- **Share my own songs** / **Play friends' songs** (music).
- **Keybinds**: F2 menu, Tab player list, T chat, G wave, B emotes, middle mouse button ping.

## Hosting tools

In the F2 panel, under each player:

- **may drive**: untick to stop them taking the driver's seat (they can still ride as a passenger).
- **shared money**: untick to give them their own wallet. Their earning and spending no longer changes yours.
- **Kick** removes them from the game.
- **Lock the game** below the list: nobody new can join. Players who were already in this session can still come back.

## Testing alone

**Test: see yourself and your bots as friends see you** in the F2 panel puts a copy of you (and of your bots) next to you, drawn exactly the way a friend's game draws you: walking, crouching, leaning, looking around, emotes, drunkenness, driving. Useful to check things without a second player.

## Good to know

- Both players need the **same mod version** and an **up-to-date game**. The mod tells you if they don't match.
- Other mods may work, but anything that changes the world (new items, extra cars) only exists for players who have the same mods, and isn't synced. Test with as few other mods as possible first.
- **Death is real** in My Summer Car. If a friend dies, the game deletes their temporary copy, and after a few seconds they **respawn in the host's world by themselves**, starting fresh. The host's world is untouched. If the **host** dies, the host's save is gone, just like in single player.
