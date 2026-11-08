---
name: dotnet-conventions
description: How C#/.NET code is written, built and tested in the UOCS repositories (MondainShard on ModernUO, MondainGem, MondainGate on ClassicUO, UOMapWeaver). Use before writing or changing any C#, csproj, sln or test code there.
---

# C#/.NET conventions in UOCS

All four C# repos target **.NET 10** (`net10.0`). Tests are **xUnit** (`MondainGem.Tests`, `UOMapWeaver.Tests`, `Projects/Server.Tests` and `Projects/UOContent.Tests` in MondainShard, `tests/ClassicUO.UnitTests` in MondainGate). Identifiers, comments and commits in English.

## Everywhere

- Follow the repo's own `.editorconfig` (MondainShard and MondainGate have one; MondainShard also `stylecop.json` and `Rules.ruleset`). Do not reformat code you did not change: forks stay easy to merge with upstream only if diffs stay small.
- Every behaviour change gets a test in the repo's test project; a test names the concrete case (input, expected output).
- No new NuGet package without saying why in the PR; check its licence (no GPL in MondainGate, MondainGem or the website).
- Deterministic generation stays deterministic: same seed, same output. No `DateTime.Now`, `Random.Shared` or unordered `Dictionary` iteration in generation code.
- Never trust client data on the server: lengths, coordinates and ids from packets are checked before use.

## MondainShard (ModernUO fork, GPL-3.0)

- Custom shard content lives in `Projects/UOContent/CursedSoulsContent/<System>/`; keep upstream files untouched where a custom file can do the job, and mark unavoidable upstream edits with a short `// CursedSouls:` comment (decision 2026-11-02; the same holds for ClassicUO files in MondainGate).
- Persistence uses ModernUO's source-generated serialization (`[SerializationGenerator(<version>)]`, `[SerializableField]`). Changing saved fields means bumping the version and committing the generated `Migrations/*.v<N>.json` file; CI fails on an uncommitted migration (`git diff --exit-code ./**/Migrations/*.v*.json`). A save-format change is reviewed at `xhigh`.
- Build like CI: `./publish.sh release` (calls `dotnet publish` after `dotnet tool restore`), then `dotnet test --no-restore`. On Linux `publish.cmd` (what CI runs) just calls `publish.sh`; a cold run took 55 s on 2026-11-02. `publish.sh` exits with the schema generator's status, so a failed restore still returns 0: read the log for `error` lines. `TreatWarningsAsErrors` makes NuGet audit warnings (NU1902/NU1903) fail the restore.
- ModernUO's own Claude skills live in `dev-docs/claude-skills/` (on `develop` and later; maintained upstream: serialization, timers, gumps, commands and targeting, configuration, content patterns, code audit, threading, networking, regions, events, RunUO migration). Read the one that matches the code you touch before writing it; do not copy them into `.claude/`.
- Upstream: `modernuo/ModernUO`. Upstream merges continue (decision 2026-11-02): keep the framework and package versions upstream uses, keep custom code in custom files, and keep upstream files as close to upstream as possible so merges stay cheap. The same holds for MondainGate and `ClassicUO/ClassicUO`.

## MondainGem (world generator, no licence file: private)

- `MondainGem.Core` is a library with no UI dependency; `MondainGem.CLI` is the wizard; `MondainGem.Tests` holds unit, integration and debug-visualisation tests.
- Chunks are 8×8 tiles; the binary chunk format (`.mgsc`, `Chunks/ChunkSerializer.cs`) and the network form (`Chunks/StreamedChunkData.cs`) are shared with MondainGate and the server: changing either is a contract change (architect first, version bump, round-trip test).
- Build and test: `dotnet build MondainGem.Tests`, `dotnet test MondainGem.Tests`, `dotnet test --filter "<expr>"` for one test. The solution is `MondainGem.slnx`.

## MondainGate (ClassicUO fork, BSD-2-Clause)

- Needs the `MondainGem` repo as a sibling folder (ProjectReference `..\..\..\MondainGem\MondainGem.Core\MondainGem.Core.csproj`) and its submodules (`git submodule update --init --recursive`: FNA, MP3Sharp, FileEmbed).
- Custom packets and their sizes are overridden in `src/ClassicUO.Client/Network/PacketsTable.cs` (custom ids forced to variable length); handlers in `Network/PacketHandlers.cs`, outgoing in `Network/OutgoingPackets.cs`; chunk streaming in `src/ClassicUO.Client/Game/Map/Streaming/`. A new or changed packet is a contract change shared with MondainShard.
- Build and test from the repo root: `dotnet build`, `dotnet test`.

## UOMapWeaver (GPL-3.0)

- `UOMapWeaver.Core` (conversions, no UI), `UOMapWeaver.App` (Avalonia 11 UI), `UOMapWeaver.Tests` (xUnit, includes round-trip MUL/BMP tests and app smoke tests).
- Build and test: `dotnet build UOMapWeaver.sln`, `dotnet test UOMapWeaver.sln`. `AppSmokeTests` need a display: headless (cloud, CI) run `xvfb-run -a dotnet test UOMapWeaver.sln` (12/12 pass), without it two fail with `XOpenDisplay failed`.
- Releases come from `.github/workflows/release.yml` (`dotnet publish` of the App per OS).
