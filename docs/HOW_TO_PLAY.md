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
- **Riding along**: you can stand on the boat, in the back of the van or on a trailer while somebody drives. You move with it, and everyone sees you on it.
- **Your own body**: look down to see your character. Emotes are shown from the front for a moment (both can be switched off in the settings).
- **Waving**: press **G**. **Emotes**: press **B** and pick one (number keys or click): wave, point, cheer, shrug, drink, facepalm, sit down, dance.
- **Finding each other**: friends who are far away or behind you get a marker with their name and distance. Press the **middle mouse button** to ping: everybody sees a marker where you're looking for 10 seconds. The host can teleport: in the F2 panel, **Go to** next to a player teleports the host to them, and **Bring** teleports that player to the host (on foot only).
- **How friends are doing**: the player list (Tab / F2) shows if someone is drunk, hungry, thirsty, tired or needs the toilet. Drunk friends sway; sleeping friends lie in bed.
- **Chat**: press **T**, type, press **Enter**. **Esc** cancels.
- **Player list**: hold **Tab**. The F2 panel also shows everyone's status and ping.

## The map

Press **M**. It's a picture of the whole world taken from above when you load in.

- **Drag** to move it, **scroll** to zoom (around the mouse), **C** centres it on you.
- You see yourself, your friends (name, which way they face, and whether they're on foot, driving the van, riding the moped, on the boat...), parked vehicles, bots and pings.
- **Click** to add waypoints and plan a route; **right-click** a waypoint to remove it, **Backspace** removes the last one, **Clear route** removes all. The next waypoint is also shown in the world with its distance, and ticks off when you get there.
- **Share my route** shows your route on your friends' maps, and where it ends in their world.

## Together

- **Hand an item over**: hold it, look at a friend close by and press **H**. It floats in front of them; they click to take it.
- **Chat bubbles**: what someone says in chat shows over their head for a few seconds.
- **Lock your car**: in the F2 panel, *Lock the ...* for the vehicle you're in or next to. Only you can drive it then (the host can always unlock it).
- **Cars knock you down**: a car somebody else drives knocks you over if it hits you at speed (the host can switch this off in the F2 panel).
- **Watch a friend**: **Watch** next to a player in the F2 panel follows them from behind. **Esc** stops.
- **Sleeping**: time only passes when the host sleeps. When the host goes to bed, everybody's screen dims while the night passes.
- **Connection**: the player list shows how good each connection is (good / ok / poor, ping, lost updates), and warns you when it gets bad.
- **Shopping list**: in the F2 panel. Anyone can add, tick and remove items; an item ticks itself when one turns up in the world (bought at Teimo's...). The host's list is kept between sessions.

## Races

The host plans a track on the map (M): the route's waypoints are the checkpoints, the last one is the finish. In the F2 panel, under **Race**, pick the number of laps and press **Start (3-2-1)**. Everyone in the world gets a countdown, the next checkpoint in the world, their time and place. Results show at the end (the host can end a race early).

## More together

- **Carry a friend**: look at a friend who is asleep, passed out or knocked down and press **H** to pick them up. Press **H** again to put them down (they can also press it to get down).
- **Towing**: when vehicles are joined (tow rope, trailer), the game of whoever drives simulates all of them, so they don't pull against each other.
- **Dents**: crash damage looks the same for everyone, also for players who join later.
- **What happened**: a short list in the F2 panel of who did what (joins, driving, money, deaths, knock-downs, hand-overs...).
- **Session summary**: when the host saves at the toilet (or from the F2 panel), everyone sees how far each player walked and drove, money earned and spent, how drunk they got and more.
- **Restore points**: in the main menu, the host's F2 panel lists the last 6 versions of their save. **Restore** (click twice) puts one back; the current save is kept as a restore point first, so it can be undone.
- Friends' places and needs are also saved every 3 minutes, so a crash of the host's game doesn't lose where everyone was.

## One world

- **Traffic and people are the same for everyone**: the cars on the roads, the police and other NPC cars, the bus, the train, the rally cars, the villagers walking around and the animals all follow the host's game. Something the host's game has switched off because the host is far away keeps running in your own game near you, so nothing disappears in front of you.
- **If the host's game is gone** (crash, lost connection), the next player keeps playing and becomes the host. Everyone else goes back to the menu and reconnects to them by themselves. The new host's own save isn't touched: when they stop hosting, the world they took over is kept as a restore point and their own save comes back. Switch this off in the settings (*If the host's game is gone, the next player takes over*).

## Music

Put your own `.ogg` songs in the game's `Radio` or `CD1`–`CD3` folders, as in single player. When you play one on the radio or the CD player, your friends hear the same song from the same speaker, at the same moment. If they don't have the song, their game fetches it from you through the host first (a few seconds), and keeps it in `music_cache` in the mod's settings folder so it only travels once. The cache cleans itself up above 500 MB.

Two switches in the mod settings: **Share my own songs** and **Play friends' songs**.

## Bots

Bots are the host's tool (or yours when you play alone). Press **F2** in the world and click **Spawn bot**. Everyone in the game sees your bots (with your name in their name tag). Pick **All** or a bot's number, then give an order:

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
- **Keybinds**: F2 menu, Tab player list, T chat, G wave, B emotes, middle mouse button ping, M map, H hand over.

## Hosting tools

In the F2 panel, under each player:

- **Go to** teleports you to them; **Bring** teleports them to you (both on foot). Only the host can teleport.
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
