# Building from source

## Requirements

- Windows with My Summer Car and MSCLoader installed (the build references your own game files; none are in this repository).
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (only for building; the mod itself targets .NET 3.5 like every MSCLoader mod).

## Build

```powershell
.\scripts\build.ps1            # builds build\MSCMP.dll
.\scripts\build.ps1 -Install   # builds and installs into your game
.\scripts\package.ps1          # builds the release zip in artifacts\
```

The scripts find the game through your Steam libraries. If yours is somewhere unusual, pass `-GameDir "D:\Games\My Summer Car"` or copy `config\local.example.json` to `config\local.json` and edit it.

Without the scripts:

```powershell
dotnet build src\MSCMP\MSCMP.csproj -c Release -p:GameDir="C:\...\My Summer Car"
```

## The API audit

Mods are compiled against Microsoft's .NET 3.5 reference assemblies, but the game runs Unity 5's Mono 2.0, which lacks some of those APIs. A missing one compiles fine and then crashes in game with a `TypeLoadException`/`MissingMethodException`. That's what broke an earlier version of this project.

`tools\ApiAudit` reads the built `MSCMP.dll` and checks that **every** type and member it references exists in the game's own `mysummercar_Data\Managed` assemblies. `build.ps1` runs it automatically and fails the build if anything is missing:

```powershell
dotnet run --project tools\ApiAudit -- src\MSCMP\bin\Release\net35\MSCMP.dll "C:\...\My Summer Car\mysummercar_Data\Managed"
```

## Rules for contributions

- Never commit game files: no DLLs from `Managed`, no decompiled game code, no assets, no saves, no logs. `.gitignore` blocks the usual suspects.
- The host must never modify its own save. Clients only ever replace their save with a backup in place (see `SaveSync`).
- Everything that touches the game goes through `try/catch`. A multiplayer problem must never crash someone's game.
- Keep `MSCMP.dll` dependency-free: Harmony, Steamworks.NET, PlayMaker and UnityEngine come from the game and MSCLoader.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how the pieces fit together.
