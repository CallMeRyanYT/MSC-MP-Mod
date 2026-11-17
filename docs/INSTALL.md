# Installing MSC Multiplayer

Both players need to do this, and both need the **same version** of the mod. It takes about five minutes.

## What you need

- **My Summer Car on Steam** (Windows). The mod connects players through Steam, so both players must start the game from Steam and be Steam friends.
- **MSCLoader** 1.4 or newer, the standard My Summer Car mod loader.
- The game must be **up to date** on both PCs (Steam updates it automatically).

## Step 1: Install MSCLoader (skip if you already use mods)

1. Go to [MSCLoader on Nexus Mods](https://www.nexusmods.com/mysummercar/mods/147) (or the [GitHub releases](https://github.com/piotrulos/MSCModLoader/releases)) and download the latest MSCLoader.
2. Unpack the zip **somewhere outside the game folder**, for example on your Desktop.
3. Run **`MSCLInstaller.exe`**. It finds your game by itself. Click **Install MSCLoader**. When it asks where mods should go, the default (**Game folder**) is fine.
4. Start My Summer Car once. You should see the MSCLoader logo and an MSCLoader **Settings** button in the main menu. Then quit the game.

## Step 2: Download MSC Multiplayer

1. Open the [latest release](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases/latest).
2. Under **Assets**, download **`MSCMP-x.y.z.zip`**.
3. Right-click the zip → **Extract All...** → extract it anywhere (for example your Downloads folder).

## Step 3: Run the installer

1. Make sure My Summer Car is **closed**.
2. Open the extracted folder and double-click **`Install.bat`**.
3. A window opens and does everything:
   - finds My Summer Car in any of your Steam libraries (if it can't, it asks you to pick the folder),
   - checks that MSCLoader is installed,
   - removes older versions of this mod (including the old `MSCMP.Mod.dll`),
   - copies `MSCMP.dll` into your Mods folder.
4. When it says **All done!**, press Enter to close it.

If Windows shows *"Windows protected your PC"*, click **More info** → **Run anyway**. The installer is a plain text script (`install.ps1`), so you can open it in Notepad and read exactly what it does.

## Step 4: Check that it works

1. Start My Summer Car **from Steam**.
2. In the main menu a small **MSC Multiplayer** panel appears in the top-right corner. If it's hidden, press **F2**.
3. In MSCLoader's mod list you'll see **MSC Multiplayer 0.12.0**.

Now read **[How to play](HOW_TO_PLAY.md)**.

## Installing by hand (if the installer can't run)

1. Find your Mods folder. Open `My Summer Car\doorstop_config.ini` in Notepad and look at the `mods =` line:
   - `mods = GF` → `...\steamapps\common\My Summer Car\Mods`
   - `mods = MD` → `Documents\MySummerCar\Mods`
   - `mods = AD` → `%USERPROFILE%\AppData\LocalLow\Amistech\My Summer Car\Mods`
2. Delete `MSCMP.Mod.dll` from that folder if it's there (an old version).
3. Copy **`MSCMP.dll`** into that folder.

## Updating

Download the new release and run its `Install.bat`. It replaces the old version. Both players must update.

## Uninstalling

Double-click **`Uninstall.bat`**. It removes the mod and, if a multiplayer session was interrupted (crash, power cut), it puts your own save back first. Your settings and save backups are left in place.
