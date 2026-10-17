# MSC-MP

Cooperative multiplayer development for **My Summer Car**.

**v0.5.0 adds a front passenger in the Hayosiko and host-owned shared save/resume for the supported world. The owner reported the previous build working; these new passenger and save integrations still need in-game verification. Use disposable profiles.**

Shared mode covers 22 persistent pickup items and the Hayosiko. Native Save & Quit now captures the shared checkpoint before releasing native saving. An offline tool bundles the disposable native files, checkpoint and protected PlayerPrefs. It does not add live synchronization for unsupported progression or vehicles. See [passenger and shared-save guide](docs/SHARED_SAVES.md).

[Download experimental build](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases) · [Install and playtest with a friend](docs/INSTALL.md) · [Report a problem](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues/new/choose) · [Feature status](docs/FEATURE_SUPPORT.md)

## What is available?

| Area | Current state |
| --- | --- |
| Host / Join / Leave | Private LAN/tailnet sessions, matching-build checks, ping and reliable probes |
| Characters | Original low-poly male/female meshes, portraits, walking, carrying and seated poses; visual verification still needed |
| Remote walking | Starts after the baseline; host-approved positions, correlated rejection correction and bounded movement history; game validation pending |
| Error feedback | Automatic local mod-error reports, F7 capture and a player note |
| 22 persistent items | Exclusive holder, pickup/drop/throw, compound colliders and host body simulation; special use actions gated. [Exact catalogue](docs/SHARED_ITEM_CATALOGUE.md) |
| Hayosiko | Driver plus one front passenger, seat conflicts, stationary entry/exit, host physics, replica seats and disconnect cleanup; doors/dashboard disabled |
| Authority and replication | Bounded late join, host checkpoint save/resume, stable item IDs and fresh session identity |
| Wider game | Dynamic items, item use, other vehicles, assembly and live economy/progression sync remain unfinished |

This is an early development project. See the [known limitations](docs/KNOWN_ISSUES.md).

## Give it to a friend

1. Send your friend the [Releases page](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases). Download the attached **MSC-MP-Mod-0.5.0-experimental.zip**, not GitHub's source-code ZIP.
2. Each player needs their own My Summer Car installation and MSCLoader. The inspected target is **Steam build 20171487 / Unity 5.0.0f4 / MSCLoader 1.4.2.410**.
3. Follow [installation and save isolation](docs/INSTALL.md) on each PC. Both players must use the same package and compatible game/mod files.
4. Load the disposable game first. Both enable **Shared world**. One player chooses **Host shared world**; the other chooses **Join** using the host's reachable numeric IPv4 address and port **20577**.
5. Wait for **Shared items and van ready (22 items)**. Follow the [end-of-build checklist](docs/PLAYER_CHECKS.md) when ready to test.

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

Legacy automated checks cover authority, three-client UDP fanout, world bootstrap, driver controls, character messages, feedback and mod-only persistence, plus deployment/package and profile-recovery fixtures. The fanout regression sends 100,800 addressed payloads across sequence wrap; it fails against the previous socket source. A metadata audit checks the built DLL against the actual shipped game libraries. These checks do not prove working multiplayer inside the game. Exact current build and test evidence is in the ignored build manifest and test results.

Observed v0.2.0 single-game diagnostics include menu/GAME initialization, loader-ready callbacks, local hosting, F7 reporting and read-only bindings. The observed join attempt failed. The owner subsequently reported the 0.4.0 build working, without a per-scenario test record. The new 0.5.0 passenger and save/reload behavior still requires the final game checks. See [runtime evidence](docs/RUNTIME_EVIDENCE.md).

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
- Verify passenger travel, save/reload and host save transfer using the final checklist.
- Extend supported vehicle families, assembly and wider world systems with inspected game bindings.

[Compatibility](COMPATIBILITY.md) · [Architecture](ARCHITECTURE.md) · [Protocol](PROTOCOL.md) · [Testing](docs/TESTING.md) · [Changelog](CHANGELOG.md) · [Project brief](docs/PROJECT_BRIEF.md) · [Third-party notices](THIRD_PARTY_NOTICES.md)

Unofficial project. Not affiliated with or endorsed by Amistech Games. My Summer Car and its assets belong to their respective owners.
