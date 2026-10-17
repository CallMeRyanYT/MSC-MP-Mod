# Compatibility and discovery

Inspection date: **2026-10-17**. This is an **inspected development target**, not a claim of tested multiplayer compatibility. Discovery read installed binaries, assembly metadata, the loader's XML documentation, configuration, the Steam app manifest, and directory listings. It did not launch the game, execute game assemblies, or read/write save contents.

## Installed target

Paths below are relative to `MSC_GAME_DIR`, except the Steam manifest and per-user save location. `scripts/inspect.ps1` writes absolute paths and full fingerprints to ignored `.local/discovery.json`. Build-specific source/reference fingerprints are recorded in `artifacts/current/build-manifest.json`; neither file belongs in Git.

| Fact | Observed value | Evidence and limit |
| --- | --- | --- |
| Game | My Summer Car, Steam app **516750** | `../../appmanifest_516750.acf`, `AppState.appid` and `name` |
| Steam build | **20171487** | Manifest `buildid` and `TargetBuildID` agree |
| Content depot | **516751**, manifest **7688087324231130953** | Steam manifest `InstalledDepots` |
| Game version label | **v.250908-04** | First line of installed `changelog.txt`; not obtained from a running game |
| Selected beta branch | **Not established** | Inspected app manifest has no beta-branch label. Do not infer a supported branch from its absence; record the Steam selection and runtime diagnostics during the first test |
| Unity | **5.0.0f4**, revision **5b98b70ebeb9** | Native player version strings; `mysummercar_Data/mainData` also contains the serialized-file version `5.0.0f4` |
| Native process architecture | **x64 / AMD64** | PE machine `0x8664` in both `mysummercar.exe` and `mysummercar_Data/Mono/mono.dll` |
| Player file version | **5.0.0.6002871** | Windows PE version resource; this is not the gameplay version |
| Managed runtime | Legacy Unity Mono, CLR metadata **v2.0.50727** | Local UnityEngine, game and loader metadata; Mono `etc/mono/1.0` and `2.0` configuration directories present |
| Exact Mono runtime build | **Not established** | Native library has no useful Windows file version. Do not turn a Unity version or CLR metadata version into a guessed Mono release |
| MSCLoader | **1.4.2.410** | Assembly and file version in `mysummercar_Data/Managed/MSCLoader.dll` |
| MSCLoader preloader | **1.3.1.406** | Assembly and file version in `mysummercar_Data/Managed/MSCLoader.Preloader.dll` |
| Loader configuration | Enabled; `mods = GF`; redirected output enabled | `doorstop_config.ini`, target `mysummercar_Data\Managed\MSCLoader.Preloader.dll` |
| Configured mods location | Game-folder route, normally `MSC_GAME_DIR/Mods` | Installed `ModLoader.Init_GF` IL assigns `Path.GetFullPath(Path.Combine("Mods", ""))`; this depends on the launch working directory. Confirm loader's actual path in the first runtime test |
| Existing installed gameplay mods | **No gameplay DLLs found in the selected Mods tree** | At initial discovery it contained only loader console/settings asset bundles. This is a directory inventory, not a loaded-mod list |
| Runtime evidence | **Awaiting manual test** | No player output log or loader preloader log was found in the inspected installation/save directories |

The native process is x64 even though managed assemblies have PE machine `0x014c`: inspected managed assemblies are `ILOnly`, not proof of a 32-bit player. Any future native dependency must match the x64 process.

## Local references

All the following exist under `mysummercar_Data/Managed/`. They remain local build references; never package the game's or loader's assemblies with this mod.

| Assembly | Assembly version | File/product evidence |
| --- | --- | --- |
| `UnityEngine.dll` | 0.0.0.0 | Legacy single-assembly Unity layout; CLR metadata v2.0.50727 |
| `MSCLoader.dll` | 1.4.2.410 | File version 1.4.2.410 |
| `PlayMaker.dll` | 1.6.0.0 | File 1.7.7.6; product **1.7.7.f6**. These versions intentionally differ |
| `Assembly-CSharp.dll` | 0.0.0.0 | Game scripts and many PlayMaker action classes |
| `Assembly-CSharp-firstpass.dll` | 0.0.0.0 | Available; only reference when inspected code requires it |
| `Assembly-UnityScript-firstpass.dll` | 0.0.0.0 | Contains `CharacterMotor`; presence does not establish the active player binding |
| `mscorlib.dll`, `System.dll`, `System.Core.dll`, `System.Xml.dll` | 2.0.5.0 | File version 3.0.40818.0; Unity's installed legacy profile |
| `ES2.dll` | 0.0.0.0 | Easy Save 2 API present; not a verified isolation mechanism |
| `0Harmony.dll` | 1.2.0.1 | Present; the diagnostic milestone does not need patches |
| `NAudio.dll` / `NVorbis.dll` | 1.10.0.0 / 0.8.6.0 | Loader dependencies present |
| `Newtonsoft.Json.dll` | 13.0.0.0 | File version 13.0.4.30916 |
| `INIFileParser.dll` / `Ionic.Zip.Reduced.dll` | 2.5.2.0 / 1.16.1.0 | Loader dependencies present |

The loader also declares an `MSCCoreLibrary, Version=1.0.0.3` reference; that DLL was not in the selected Managed/Mods inventory. Inspected loader scene/save call sites check `IsReferencePresent("MSCCoreLibrary")` before invoking its scheduler. This is not evidence of a broken loader installation; actual assembly resolution remains part of the first load test.

## Compilation, dependencies and fingerprints

Use the installed legacy Unity API surface. The current [maintainer setup guide](https://github.com/piotrulos/MSCModLoader/wiki/Install-Visual-Studio-for-MSCLoader) specifies the .NET Framework 3.5 workflow and distinguishes it from newer tooling. Local `MSCLoader.dll` references `System.Core` 3.5.0.0 and carries CLR v2 metadata. A modern compiler may emit legacy-compatible IL, but it cannot make modern runtime APIs available in this game. A modern test runner also does not prove Mono compatibility.

The installed Unity BCL copies are **runtime inventory, not a consistent compiler reference set for this loader**. In particular, local `System.Core.dll` defines `System.Action` under assembly identity 2.0.5.0, whereas `Mod.SetupFunction` expects the standard Framework `System.Core, Version=3.5.0.0`. Compiling against only the game's BCL copies produced CS0012 for that mismatch. Use standard .NET Framework 3.5 references (`mscorlib`/`System` 2.0.0.0 and `System.Core` 3.5.0.0) with the installed UnityEngine, MSCLoader and PlayMaker references. UnityEngine and PlayMaker themselves reference those standard BCL identities. Do not copy those framework reference assemblies into the game or release package; successful in-game resolution still needs testing.

Version 0.2.0 pins the legacy **LiteNetLib 0.7.7.2** source at commit `5ba2470394092ac5924b1ea45da588cfa5a1e948`, with MIT license, exact hashes and documented local patches. It compiles with the installed C# 3 compiler and passes headless UDP/impairment tests. **It has not loaded inside MSC's Mono runtime yet.** See `dependencies.lock.json` and `docs/TRANSPORT.md`. Modern LiteNetLib versions are not substituted.

The discovery tooling retains full SHA-256 fingerprints in `.local/discovery.json`. Compare the gameplay assemblies **and serialized scene/content files** when defining a supported build: much of this game's behavior is stored in PlayMaker data. Steam build ID, assembly version 0.0.0.0, names, or one script hash alone are insufficient as an admission check. Do not treat this report as an implemented connection allowlist.

Future asset bundles must use the verified Unity version. The [maintainer asset-bundle guide](https://github.com/piotrulos/MSCModLoader/wiki/How-to-create-and-use-Asset-Bundles) specifies Unity 5.0.0f4. No character bundle or asset-authoring editor is required for the diagnostic milestone.

## Save and runtime boundaries

The standard per-user directory `%USERPROFILE%/AppData/LocalLow/Amistech/My Summer Car` exists locally and contains existing `defaultES2File.txt`, `items.txt`, `meshsave.txt`, `graveyard.txt`, `trophies.txt`, `options.txt` and loader/mod data. Only filenames and filesystem metadata were inspected. Existing third-party-mod residue is evidence of prior use, not evidence that that mod is currently loaded. These files must be treated as the user's personal data.

Installed code contains `ES2.Save`, `ES2Writer.Save`, PlayMaker save actions, `UniqueSaveManager.OnApplicationQuit`, and `MSCLoader.SaveLoad` persistence methods. Their presence identifies investigation targets; it does **not** establish every active write path. The loader's `OnSave` callback is documented as occurring after saving and cannot be used as proof that client writes have been prevented.

Client save isolation is **not implemented or proved**. A second game folder, a backup of one file, or changing one ES2 filename does not establish isolation. No Host/Join gameplay should be enabled until ordinary saves, scene transitions, quit, player settings, dependency writes and crash recovery have a tested policy. Use a separately isolated disposable Windows test profile for initial gameplay diagnostics; do not automatically create a new game over this profile's existing data.

`mainData` contains authored scene strings for `SplashScreen`, `MainMenu`, `Intro`, `GAME`, and `Ending`. Installed loader IL recognizes `MainMenu`, `Intro`, `GAME`, and `Ending`. Neither static observation proves the actual callback sequence, late object construction, focus behavior, pause behavior, scene reload cleanup, or loaded mod state. Capture those in game before enabling gameplay adapters.

## Acceptance gates

| Gate | Evidence required | Current status |
| --- | --- | --- |
| Static target discovery | Versions, native architecture, reference paths and loader configuration | Inspected locally |
| Reproducible diagnostic build | Build against local references; inspect emitted references and package contents | See build output and handoff; not implied by discovery |
| Real loader initialization | Menu log/overlay, actual loader/game versions, successful diagnostic component startup | Awaiting manual test |
| Lifecycle cleanup | Disposable game load, scene exit/re-entry, disable/re-enable and process exit without duplicate diagnostics or leaked objects | Awaiting manual test |
| Background/pause behavior | Focus loss and menu pause measurements using a monotonic timer | Awaiting manual test |
| Gameplay bindings | Actual player hierarchy, scoped item/FSM bindings, stable entity resolver and construction timing | Unverified; disabled |
| Client save isolation | Proved coverage of active save paths and recovery; original save preserved | Unverified; gameplay sessions disabled |
| Transport | Pinned compatible dependency loaded by two game processes, bounded exchange and disconnect | Not selected; not tested |
| Two/four-player gameplay | Milestone acceptance scenarios in actual game processes | Not tested |

See [bindings evidence](docs/BINDINGS.md) for the distinction between observed API members and unverified world integration.
