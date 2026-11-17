# Third-party notices

MSC Multiplayer ships a single file, `MSCMP.dll`, plus the installer scripts. It contains no code or assets from other projects.

At runtime it uses libraries that are already part of your game or of MSCLoader. They are **not** distributed with this mod:

- **My Summer Car** by Amistech Games: Unity engine, game code, PlayMaker state machines, character models, clothes and animations. Player models are rebuilt at runtime from the copy of the game you own; nothing is extracted or redistributed.
- **Steamworks.NET** and the Steamworks API (Valve), shipped with the game.
- **MSCLoader** by piotrulos, and **Harmony** by Andreas Pardeike (MIT), shipped with MSCLoader.

Build-time only:

- **Microsoft.NETFramework.ReferenceAssemblies** (MIT) for compiling against .NET 3.5.
- The build references your own installed game assemblies (`mysummercar_Data\Managed`). They stay on your PC and must never be committed or packaged.

Large parts of this mod were written with the help of AI (Claude by Anthropic).
