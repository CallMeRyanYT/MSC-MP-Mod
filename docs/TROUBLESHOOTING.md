# Troubleshooting

## The MSC Multiplayer panel doesn't show up

- Press **F2** in the main menu.
- Check that MSCLoader works: the main menu should show the MSCLoader logo and a **Settings** button. If not, reinstall MSCLoader ([INSTALL.md](INSTALL.md#step-1-install-mscloader-skip-if-you-already-use-mods)).
- Open MSCLoader's mod list. If **MSC Multiplayer** is missing or shows an error, run `Install.bat` again and look at the [log files](#log-files).
- If the panel says *Steam is not running*: start the game **from Steam**, not by double-clicking the exe.

## My friend can't see the invite / can't join

- Both players must start the game **from Steam** and be **Steam friends**.
- The **Steam overlay** must be enabled (Steam → Settings → In Game → *Enable the Steam Overlay while in-game*). The invite window and invite pop-ups use it.
- The friend can also open the Steam friends list, right-click the host and choose **Join Game**. They have to be in the main menu.
- Both players need the **same version** of MSC Multiplayer. If not, the friend gets a message saying which version the host has.
- Both games must be up to date. Steam → right-click My Summer Car → Properties → Installed Files → **Verify integrity**, then reinstall MSCLoader if Steam replaced files.
- Connections go through Steam, so no ports need to be opened. A VPN or a very strict firewall can still slow down the first connection. Give it up to a minute.

## The friend is stuck on "Getting the host's world..." or "Loading..."

- The host's world is a few MB and normally arrives in seconds. If it stops, the friend clicks **Leave** and joins again.
- If the host is still in the main menu, the friend waits on *Waiting for the host to press Continue*. That's normal.
- Loading My Summer Car takes a while on slower PCs. The mod waits up to five minutes for a loading player before giving up.

## Something isn't in the same place for both players

Small differences can happen in this test version. Please [report them](#reporting-a-bug) with what you did. Quickest fix: the friend clicks **Leave** in the F2 panel and joins again. They get a fresh, exact copy of the host's world.

## My own save is gone!

It isn't. Before a friend gets the host's world, their own save is copied to:

```
%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\MSCMP_backups\<date-time>
```

It is restored automatically when you leave a session, or the next time the game starts after a crash. If that ever fails, close the game and run **`Uninstall.bat`** (it restores the backup), or copy the files from the newest backup folder back into `...\Amistech\My Summer Car` by hand.

The host's save is never changed by the mod.

## Log files

When something goes wrong, these files tell us what happened:

1. **The mod's log:** `My Summer Car\Mods\Config\Mod Settings\MSCMP\mscmp.log` (plus `mscmp.previous.log` from the last run). If your Mods folder is in Documents or AppData, it's under that Mods folder instead. MSCLoader's settings page for MSC Multiplayer has an **Open log folder** button too.
2. **The game's log:** `My Summer Car\output_log.txt`.

For more detail, turn on **Detailed log** in the MSCLoader settings for MSC Multiplayer, reproduce the problem, and send the logs.

## Reporting a bug

[Open an issue](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues/new?template=bug_report.md) and include:

- what you did, what you expected and what happened,
- whether you were the host or the friend,
- `mscmp.log` from **both** players (and `output_log.txt` if the game showed errors),
- your mod version and other installed mods.

Please don't upload your save files.
