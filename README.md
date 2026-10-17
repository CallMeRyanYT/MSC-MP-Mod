# MSC-MP

Cooperative multiplayer development for **My Summer Car**.

**v0.3.0 adds experimental coffee-cup pickup/drop/throw and Hayosiko driver/body synchronization. These game adapters are implemented and built, but not verified in an actual multiplayer game. This is not full-game multiplayer. Use disposable test profiles.**

Shared mode requires the disposable-profile marker and the host's world baseline before interaction. Other items, passenger seats, other vehicles, assembly, progression and coherent vanilla saves remain unfinished. See [vehicle binding scope](docs/VEHICLE_BINDING_EVIDENCE.md).

[Download experimental build](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases) · [Install and playtest with a friend](docs/INSTALL.md) · [Report a problem](https://github.com/CallMeRyanYT/MSC-MP-Mod/issues/new/choose) · [Feature status](docs/FEATURE_SUPPORT.md)

## What is available?

| Area | Current state |
| --- | --- |
| Host / Join / Leave | Private LAN/tailnet sessions, matching-build checks, ping and reliable probes |
| Characters | Original low-poly male/female meshes, portraits, walking, carrying and seated poses; visual verification still needed |
| Remote walking | Starts automatically when the shared baseline is ready; actual two-PC game validation pending |
| Error feedback | Automatic local mod-error reports, F7 capture and a player note |
| Coffee cup | Exclusive holder, pickup/drop/throw and host body simulation; drinking/pouring disabled in shared mode |
| Hayosiko | One driver, validated controls, host physics, replicated body, seat cleanup and stale-input braking; doors/dashboard disabled |
| Authority and replication | Automated synthetic-world tests, bounded late join and mod-only checkpoints |
| Wider game | Other items/vehicles, passengers, assembly, economy and coherent vanilla persistence remain unfinished |

This is an early development project. See the [known limitations](docs/KNOWN_ISSUES.md).

## Give it to a friend

1. Send your friend the [Releases page](https://github.com/CallMeRyanYT/MSC-MP-Mod/releases). Download the attached **MSC-MP-Mod-0.3.0-experimental.zip**, not GitHub's source-code ZIP.
2. Each player needs their own My Summer Car installation and MSCLoader. The inspected target is **Steam build 20171487 / Unity 5.0.0f4 / MSCLoader 1.4.2.410**.
3. Follow [installation and save isolation](docs/INSTALL.md) on each PC. Both players must use the same package and compatible game/mod files.
4. Load the disposable game first. Both enable **Shared world**. One player chooses **Host shared world**; the other chooses **Join** using the host's reachable numeric IPv4 address and port **20577**.
5. Wait for **Shared cup and van ready**. Follow the [end-of-build checklist](docs/PLAYER_CHECKS.md) when ready to test.

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
