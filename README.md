# MSC Multiplayer

**Play My Summer Car together with your friends.** Invite them through Steam, they load straight into your world, and you build the Satsuma, drink beer at the lake and crash the van together.

> **Status: 0.11.0, an early test build of a complete rewrite.** Connecting works; the first two-player tests are under way. If something breaks, please [open an issue](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues) and attach the log file ([where to find it](docs/TROUBLESHOOTING.md#log-files)).

## What it does

- **Steam invites.** No IP addresses, port forwarding, Hamachi or Tailscale. Invite a friend from the game and they click *Join*.
- **You share one world.** Your friend gets an exact copy of your save when they join. Their own save is backed up first and put back automatically when they leave, even if the game crashed.
- **Real My Summer Car people as player models.** The players are rebuilt from the game's own character models, clothes and animations. They walk, run, crouch, look around, sit in cars, steer and wave. You can pick your body and clothes in the mod settings or the F2 panel, with a live preview.
- **Everything you pick up is shared.** Carrying, dropping and throwing items and car parts works for everyone. Things the game creates (removed parts, bought groceries) appear for everyone too.
- **All the cars.** One player drives and everyone else sees the wheels turn, the suspension work and the engine rev. You can ride in the passenger seat when a friend drives.
- **The world reacts to everyone.** Doors, light switches, taps, the TV and radio, dashboard buttons, the ignition, and building the car together: assembling and removing parts, tightening every bolt, wiring.
- **One clock, one sky, one wallet.** Everybody has the host's time of day and weather. Money is shared between all players. Hunger, thirst and the other needs stay separate for each player.
- **Liquids and wear too.** Fuel, oil, coolant, brake and clutch fluid, battery charge, wear, damage and paint colour are the same for everyone.
- **Bots that help out.** The host can spawn helpers from the F2 panel that everyone in the game can see. They copy you, follow you (and ride along in your car), wander and walk to places, and do jobs: fetch the item you're looking at, collect every bottle or log into a pile, tidy up, or carry something to the shop.
- **Your friends' progress is saved too.** When the host saves, every friend's last place and needs (hunger, thirst, tiredness, drunkenness...) are saved with the world. Next time they load in, they're right where they left off. After a crash they come back to their spot. After dying they respawn by themselves.
- **Find each other.** Markers show where far-away friends are, a ping key shows everyone where you're looking, and the F2 panel can teleport you to a friend.
- **Your music, for everyone.** Songs you put in the game's Radio or CD folders play for your friends too, from the same car radio or CD player, in time. They download your songs automatically the first time.
- **Emotes.** An emote menu (B) with wave, point, cheer, shrug, drink, facepalm, sit down and dance. You can see when a friend is drunk (they sway), asleep (lying in bed), hungry or tired.
- **Host controls.** Choose who may drive and who shares your money, kick players, and lock the game.
- **Join any time.** A friend who joins later sees the world exactly as it is right now.
- **Chat, player list and name tags.**
- **No fights over objects.** The host decides who is holding or driving what, so items can't be duplicated or pulled out of someone's hands.

## Install (both players)

1. **Install MSCLoader**, the My Summer Car mod loader, if you don't have it: [Nexus Mods](https://www.nexusmods.com/mysummercar/mods/147). Run `MSCLInstaller.exe` and start the game once.
2. **Download** the latest `MSCMP-x.y.z.zip` from [Releases](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases/latest) and unzip it anywhere.
3. **Double-click `Install.bat`.** It finds your game, checks MSCLoader, removes old versions and installs the mod.

That's it. The detailed step-by-step guide is in **[docs/INSTALL.md](docs/INSTALL.md)**.

## Play

| Host | Friend |
| --- | --- |
| Start My Summer Car from Steam. | Start My Summer Car from Steam. |
| In the main menu press **F2**, then **Host game**. | Wait in the main menu. |
| Click **Invite friends** and invite them in the Steam overlay. | Accept the invite (or Steam friends list → right-click → **Join Game**). |
| Press **Continue**. | Your game downloads the host's world and loads it by itself. |

Controls in game:

| Key | What it does |
| --- | --- |
| **F2** | Multiplayer menu (invite, players, leave) |
| **T** | Chat |
| **Tab** (hold) | Player list |
| **G** | Wave |
| **Enter** at the passenger door | Ride along when a friend is driving (Enter again to get out) |

All keys can be changed in the MSCLoader mod settings. More in **[docs/HOW_TO_PLAY.md](docs/HOW_TO_PLAY.md)**.

### Saving

Only the **host's** world is saved. The host saves the usual way, with the toilet. Friends play in a copy: when a friend leaves, their own save comes back exactly as it was. The backups are in `%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\MSCMP_backups`.

## Not shared yet

Being honest about what isn't covered yet:

- Traffic, NPCs, jobs, phone calls and the shops' checkout. These run separately in each game. Anything you buy still appears for everybody.
- Minigames such as video poker and darts are per player.
- A car driven by a friend is updated 20 times a second with prediction, so very fast driving over rough ground can look slightly floaty.

## Help

- [Installation guide](docs/INSTALL.md)
- [How to play](docs/HOW_TO_PLAY.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [How it works (for developers)](docs/ARCHITECTURE.md) · [Building from source](docs/DEVELOPMENT.md)

## Credits

Made by CallMeRyanYT. Large parts of this mod were written with the help of AI (Claude by Anthropic).

My Summer Car is made by Amistech Games. MSCLoader is made by piotrulos. This mod ships none of the game's files: it uses the game you own. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
