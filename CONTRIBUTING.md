# Contributing

MSC-MP targets My Summer Car's legacy Unity runtime. Keep production source compatible with C# 3 and .NET 3.5, and verify against the pinned game/loader references described in COMPATIBILITY.md.

## Changes and checks

Explain the concrete player problem, the resulting behavior and what you actually tested. Keep lifecycle ownership explicit, bound network inputs and queues, and require host authority before gameplay mutations. New integration needs evidence of join, disconnect, scene exit and save handling; pure-logic tests alone do not establish game support.

Run `scripts/test-headless.ps1` for game-independent logic checks on Windows. GitHub Actions uses a modern .NET Framework host for this suite. For a mod change, also run the canonical inspect/build/test pipeline in README.md against your own installation; only that pipeline audits the legacy DLL dependency surface. Close the game before deployment or profile operations.

Use disposable profiles for runtime tests. Do not commit game assemblies/assets, saves, diagnostic logs, private configuration, credentials or `.local` contents. The package allowlist and fixture tests must be updated together when adding release documents.

## Issues

Use the bug-report template with versions, steps, expected/actual behavior and reviewed mod reports. Shared gameplay that the feature ledger marks unavailable is a development milestone, not an installation failure. Feature requests should describe the player interaction and how it could be tested.

## Licensing

The repository does not yet declare a license for the original MSC-MP source. Do not assume a broad redistribution grant from the dependency's license. Vendored LiteNetLib retains its MIT license and documented patches; see THIRD_PARTY_NOTICES.md. Do not contribute proprietary game code or assets.

The canonical test pipeline also requires PowerShell 7 and a local Mono.Cecil.dll (available in the local ILSpy inspection tool). Run scripts/test-runtime-api.ps1 with -CecilPath for a custom location. This audit reads the compiled mod and owned game libraries as metadata, resolving types/members without launching game code. It is separate from the headless CI checks.
