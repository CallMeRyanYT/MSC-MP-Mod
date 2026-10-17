# MSC-MP

Cooperative multiplayer development for **My Summer Car**.

**v0.2.2 is an experimental networking and character-preview build. Shared items, driving, car assembly and world saves are not synchronized. Use disposable test profiles.**

[Download experimental build](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases) · [Install and playtest with a friend](docs/INSTALL.md) · [Report a problem](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues/new/choose) · [Feature status](docs/FEATURE_SUPPORT.md)

## What is available?

| Area | Current state |
| --- | --- |
| Host / Join / Leave | Private LAN/tailnet diagnostics, compatibility checks, ping and reliable probes |
| Characters | Original low-poly male/female meshes, selectable portraits and procedural walking; visual verification still needed |
| Remote walking | Optional pose preview after connection acceptance; actual two-PC game validation pending |
| Error feedback | Automatic local mod-error reports, F7 capture and a player note |
| Game bindings | Read-only walking-player and coffee-cup inspection |
| Authority and replication | Automated tests with synthetic entities, bounded late-join snapshots and recovery |
| Shared gameplay | Items, vehicles, assembly, economy and vanilla world persistence are not integrated |

This is an early development project. See the [known limitations](docs/KNOWN_ISSUES.md).

## Give it to a friend

1. Send your friend the [Releases page](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases). Download the attached **MSC-MP-Mod-0.2.2-experimental.zip**, not GitHub's source-code ZIP.
2. Each player needs their own My Summer Car installation and MSCLoader. The inspected target is **Steam build 20171487 / Unity 5.0.0f4 / MSCLoader 1.4.2.410**.
3. Follow [installation and save isolation](docs/INSTALL.md) on each PC. Both players must use the same package and compatible game/mod files.
4. Load the disposable game first. One player chooses **Host diagnostics**; the other chooses **Join** using the host's reachable numeric IPv4 address and port **20577**.
5. Confirm acceptance and try ping/probes before enabling **Experimental walking avatars** on both PCs.

Hosting with zero peers is normal until someone joins. A scene change closes the session, so host again after entering the game. `127.0.0.1` refers to your own PC, not your friend's. LAN play needs no router port forwarding; private tailnet setup is described in the guide.

## Controls and feedback

| Key | Action |
| --- | --- |
| F6 | Check player/cup bindings without changing the world |
| F7 | Save a local problem report |
| F8 | Show/hide the diagnostics panel |
| F9 | Inspect the aimed object's collider/FSM names |
| F10 | Export a bounded diagnostic log |

Use the pause/menu screen to click panel controls. Keys can be changed in MSCLoader settings. Reports stay on the player's PC; they are **not automatically uploaded** to GitHub or a developer. See [feedback and privacy](docs/FEEDBACK.md) and the [player checklist](docs/PLAYER_CHECKS.md).

## Validation

The v0.2.2 compatibility fix passed **4,463 C# assertions**, **16 deployment/package scenarios** and **19 profile-recovery assertions**. Automated tests cover the pure logic and filesystem tools; they do not prove working multiplayer inside the game.

The new runtime API audit resolves 239 types and 748 members against the shipped game libraries. It rejects the previous DLL, which failed when receiving a connection because the game lacks `System.IO.InvalidDataException`. Both players must update to v0.2.2; real-game confirmation of the fix is pending.

Observed v0.2.0 single-game diagnostics include menu/GAME initialization, loader-ready callbacks, local hosting, F7 reporting and read-only bindings. The observed join attempt failed. Two-PC connections, character rendering, shared gameplay and complete runtime save restoration remain unverified. See [runtime evidence](docs/RUNTIME_EVIDENCE.md).

## Development

Target: legacy .NET 3.5 / C# 3. Configure ignored `config/local.json` from `config/local.example.json`, or set `MSC_GAME_DIR`. Build references come from your own game and installed legacy framework; proprietary files are never included in the repository or download.

```powershell
.\scripts\inspect.ps1
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\deploy.ps1 -WhatIf
.\scripts\deploy.ps1
.\scripts\package.ps1
```

Deployment requires the game closed, validates source/reference hashes and backs up the previous owned DLL. Uninstall/rollback only operates on the recorded unchanged mod. See [contributing](CONTRIBUTING.md).

For pure-logic checks without a game installation, run `.\scripts\test-headless.ps1` on Windows. GitHub Actions runs this suite on a modern .NET Framework host; it does not compile the Unity mod or validate legacy Mono compatibility.

## Next milestones

- Validate two actual game clients, both character options, scene transitions and profile restoration.
- Connect one inspected item family to authoritative pickup/drop/throw, with contention and late-join tests.
- Add vehicles, seats, assembly and wider world systems only after those integration gates pass.

[Compatibility](COMPATIBILITY.md) · [Architecture](ARCHITECTURE.md) · [Protocol](PROTOCOL.md) · [Testing](docs/TESTING.md) · [Changelog](CHANGELOG.md) · [Project brief](docs/PROJECT_BRIEF.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)

Unofficial project. Not affiliated with or endorsed by Amistech Games. My Summer Car and its assets belong to their respective owners.
