# Install and test with a friend

This release tests networking and character previews. It does not synchronize items, driving, assembly or world saves.

## Updating from 0.2.0

Both players must replace their old DLL with v0.2.1 while the game is closed. Version 0.2.0 can stop networking when receiving packets because it references a missing runtime exception type. Keep your existing test-profile journal and backup; use the original folder to restore that active profile, and do not prepare another profile from the new ZIP.

## Requirements

Each player needs their own game, [MSCLoader](https://github.com/piotrulos/MSCModLoader), and the exact same MSC-MP release ZIP. The inspected target is Steam build 20171487, Unity 5.0.0f4 and MSCLoader 1.4.2.410. Different game/mod fingerprints can reject a connection. No Unity editor or development tools are required to install the mod.

## Install

1. Close the game. Download **MSC-MP-Mod-0.2.1-experimental.zip** from [Releases](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases). GitHub's source-code archives do not contain the compiled mod.
2. Extract it into a permanent testing folder. Keep that folder: the save-isolation tool stores its recovery journal there.
3. Copy only `Mods/MSCMP.Mod.dll` into the Mods folder selected by your MSCLoader installation. Back up an existing MSCMP DLL before replacing it. Never copy files into the game's Managed folder.
4. Retain the included third-party license/notices. Follow save isolation below **before starting a new game**.

## Protect your existing progress

Do this independently on each PC. Close every My Summer Car process and temporarily turn off Steam Cloud for this game.

Open PowerShell in the extracted package folder:

```powershell
.\scripts\test-profile.ps1 -Action Status
.\scripts\test-profile.ps1 -Action Prepare -SteamCloudDisabled
```

Continue only after preparation succeeds. It preserves the original profile in a journaled sibling backup and records PlayerPrefs. If a disposable profile is already active, use its existing journal; do not prepare again from another extracted folder or delete the backup. Do not copy personal saves into the disposable profile.

After testing, quit the game completely and run these commands from the **same package folder**:

```powershell
.\scripts\test-profile.ps1 -Action Restore -SteamCloudDisabled
.\scripts\test-profile.ps1 -Action Status
```

Restoration archives the test profile and restores/verifies the original files and recorded PlayerPrefs. Re-enable Steam Cloud only after successful restoration. If the tool refuses an operation, retain the journal and backups and report the exact refusal. Run only one game process per Windows account.

## Connect two PCs

1. Both players load their disposable game and wait for initialization.
2. The host opens the panel with F8 and clicks **Host diagnostics**, using port **20577**.
3. The friend chooses **Join**, entering the host's reachable numeric IPv4 address and the same port.
4. Wait for connection acceptance on both sides. Try **Ping** and **Reliable probe**.
5. Enable **Experimental walking avatars** on both PCs. Select Male/Female, unpause and walk near the same coordinates. The world itself is not transferred or synchronized.

Use the pause screen to click panel controls. A host showing zero peers is simply waiting. Changing scenes closes networking; load the game before hosting, and host/join again after a scene change.

## Addresses and port forwarding

- **Same home network:** use the host PC's LAN IPv4 address. Router port forwarding is not needed.
- **Different networks:** this build is intended for a private authorized LAN/tailnet. With both PCs connected through Tailscale and access permitted, use the host's Tailscale IPv4 address. Tailscale normally works without manual port forwarding; see its [official firewall FAQ](https://tailscale.com/docs/reference/faq/firewall-ports). This mod's actual Tailscale game test is still pending.
- **Direct public IP:** inbound UDP would normally need router forwarding, but public Internet hosting is outside this release's supported scope. The transport does not provide authenticated encrypted public hosting.
- `127.0.0.1` is the PC you are using. It cannot reach a friend's PC.

If joining fails, check that the host is still listening in GAME, addresses/ports match, both builds match, and Windows Firewall permits the game on the intended private connection. Do not disable the firewall entirely.

## Report a problem

Press F7, or enter a short note and click **Report a problem**. Reports normally appear under:

`Mods/Config/Mod Settings/MSCMP/diagnostics`

Review `feedback-latest.log` and `problem-latest.log`, then attach relevant evidence to a [bug report](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues/new/choose). Include the exact failed step and both PCs' connection states. Do not post saves, game DLLs, account credentials or entire game folders.

Reports remain local. Installing the mod does not install the developer's Codex monitor, and that monitor cannot automatically read a friend's PC. Follow the [player checklist](PLAYER_CHECKS.md) and [feedback guide](FEEDBACK.md).
