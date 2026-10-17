# Third-party notices

The compiled mod embeds source from **LiteNetLib0.7.7.2**, commit `5ba2470394092ac5924b1ea45da588cfa5a1e948`, by RevenantX and contributors, under the MIT license.

- [Upstream repository](https://github.com/RevenantX/LiteNetLib)
- Full required license: `vendor/LiteNetLib/LICENSE.txt`
- Local modifications: `vendor/LiteNetLib/PATCHES.md`
- Exact source hashes: `dependencies.lock.json`

Keep this license with redistributed builds. The unused legacy serializer and upstream assembly-info file are excluded from this pinned runtime subset. No transitive networking package is required.

Character preview geometry is original procedural project code; it does not clone or redistribute My Summer Car characters.

Compilation references the user's installed UnityEngine, MSCLoader and PlayMaker assemblies and Microsoft's .NET Framework3.5 references. These remain in their installations and are not shipped. Game binaries, decompiled game source, scene data, saves and private configuration must never be packaged. Those products retain their owners' respective rights.

UnityPy, TypeTreeGeneratorAPI and ILSpy were local inspection tools; they are not runtime dependencies and their outputs/tools are excluded from the release package. See `docs/GAMEPLAY_EVIDENCE.md` for versions and method.
