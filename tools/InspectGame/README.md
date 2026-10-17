# Read-only installed-game inspection

This is development tooling only. It reads the user's existing owned game installation and writes ignored local evidence. It never patches an assembly, writes a save, changes a scene or launches the game.

Install `requirements.local.txt` into `.local/inspect-python` with a compatible local Python 3.12 runtime, then run:

```powershell
python tools/InspectGame/inspect_scene.py --game '<owned game directory>'
```

The default scene is `level2` and output is `.local/inspection/scene-level2.json`. `--scene` and `--output` select another local input/output. The parser reports unsupported components instead of treating them as established bindings. Inspection is intentionally offline and one-shot; do not transplant this world scan into a per-frame mod callback.

The generated evidence contains game-owned metadata. Keep it out of source control and release archives. Publish only the minimal summarized binding facts necessary for interoperability, as in `docs/GAMEPLAY_EVIDENCE.md`.
