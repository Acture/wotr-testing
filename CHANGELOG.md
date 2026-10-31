# Changelog

## Unreleased

- New `WotR.Testing.InGame` package (included by `WotR.Testing`): xUnit v2 tests
  marked `[Trait("WotrMode", "ingame")]` (or `both`) run inside the running game on
  Unity's main thread through `InGameFixture.OnMainThread`, with frame waits,
  reverse-order cleanup (`Own`, `Defer`), `LoadSave`, `SpawnUnit`, `Blueprint<T>`
  and distinct `[ASSET_MISSING]`, `[SCENARIO_REQUIRED]` and `[CLEANUP_FAILED]`
  failures. Install the development host once with
  `dotnet build -t:InstallWotrInGameHost`; runs never install it. See
  [in-game testing](docs/ingame.md).
- One `dotnet test` plans offgame tests, offgame fuzz targets, in-game tests and
  in-game fuzz targets from the test assembly's metadata and by default runs every
  declared partition. `WotrTestMode` (`auto`, `offgame`, `ingame`) and
  `WotrTestKind` (`all`, `tests`, `fuzz`) only narrow it; contradictions, unknown
  values and empty selections fail with `[PLAN]` before anything runs. Projects
  without in-game cases or registrations run exactly as before. Each run writes
  `wotr-run.json` with the partitions run, excluded and run by VSTest's own adapter.
- In-game partitions start their own game by default, which loads in the
  background (focus handed back, never minimized) and is closed afterwards; when the
  game is already open, the run fails with `[game-running]` instead of using it.
  `WotrInGameConnection=attach` runs in the open game and `auto` attaches or starts.
  A changed test assembly, a different mod build, a busy host or a dirty session
  fail with their cause instead of starting another game.
- `[WotrFuzz]` registrations run from `dotnet test` in the environments they name;
  findings save a replay file, and `WotrFuzzSeed`/`WotrFuzzReplay` set the seed or
  replay a case exactly. Async fuzzing (`Fuzz.RunAsync`, `FuzzTargets`) supports
  targets whose calls wait for game frames.
- With .NET 10 and `"test": { "runner": "Microsoft.Testing.Platform" }` in
  `global.json`, `dotnet test` starts a WotR test application with `--mode`,
  `--tests`, `--fuzz`, `--fuzz-seed`, `--fuzz-replay`, `--list-tests`,
  `--treenode-filter` and `--report-trx`. Option mistakes appear as failed nodes
  with their message.
- The VSTest bootstrap works from folders whose path the SDK reports with the
  `\\?\` prefix.

## 0.1.0-preview.3 - 2026-10-18

- New `WotR.Testing.Fuzzing` component: mod authors register an interface and an
  instance factory with `Fuzz.Check<IMyApi>(...)`; the library automatically
  generates method calls, argument values and stateful sequences. Failures are
  shrunk and saved for exact replay. Supports FsCheck generators and explicit
  codecs for custom argument types, inherited interfaces, overloads and Task
  methods; fresh targets are disposed after each sequence or shrink attempt.
- `WotrGameFixture.Fuzz<IMyApi>(...)` records results in `environment.json` and
  classifies unavailable Unity paths separately. All-unavailable runs fail.
  Fuzzing detects unexpected exceptions; gameplay invariants need assertions in
  the selected API or separate property tests. See [interface fuzzing](docs/fuzzing.md).
- `WotR.Testing` also includes FsCheck.Xunit 3.5.0 for author-written `[Property]`
  tests with generated inputs, custom generators, shrinking and seed replay.
- Mono installs the offline assembly resolver before SDK test discovery. This
  prevents an early game-typed field in a test adapter from caching an unresolved
  game assembly reference before the test assembly's module initializer runs.
  SDK and game method instructions remain unchanged; reports record the managed
  bootstrap's path and hash.

## 0.1.0-preview.2 - 2026-10-18

- `dotnet test` is the default entry point, including for NuGet-only consumers.
  The package automatically starts VSTest's ordinary testhost on the game's Unity
  Mono runtime and uses the project's existing adapter; discovery, Theory rows,
  filters, user runsettings, `--no-build` and standard loggers such as TRX work
  through VSTest. PowerShell is no longer required to run tests. Requires .NET SDK
  8 or later on Windows; verified with SDK 10.0.401 and xUnit v2.
- The local Mono VSTest host isolates its Json.NET assembly identity so it cannot
  replace the game's incompatible Json.NET. SDK method instructions and game
  method bodies are unchanged; both copies and host-file hashes are recorded in
  `environment.json.testHost`. No SDK cache or game files are packaged.
- `WotrRuntime=netfx` explicitly selects .NET Framework. `WotrTestTimeoutSeconds`
  (default 900) and `WotrMinimumPassed` (default 1) configure the run. Fresh reports
  are checked for the selected runtime, initialization errors, unexplained skips,
  zero passing tests and missing files. The existing PowerShell runner is retained
  for compatibility. Migration: replace script-path lookup and invocation with
  `dotnet test <project>`; replace `-Runtime netfx` with `-p:WotrRuntime=netfx`.

## 0.1.0-preview.1 - 2026-10-16

- Harmony copies: `-Harmony` (`WotrHarmony`, `WOTR_HARMONY`) chooses the
  `0Harmony.dll` a run loads: `umm` (default, as before), `game` (the game's own
  2.0.4), a file, or on the runner a `Lib.Harmony` NuGet version (`2.2.2`,
  `2.2.2/net35`). Mods and their libraries are compared with `-HarmonyReference`
  copies (default `game`); members a copy lacks are warned and listed in
  `environment.json` (`harmony`). A mod calling `CodeInstruction.LoadArgument`
  passed with UnityModManager's 2.3.6 but fails at `mod-load` with `-Harmony game`,
  as it does in such a game.
- The library's own patches work on Harmony 2.0.4: the Unity log boundary no longer
  uses `__args`, and rewritten internal calls end with an unreachable return. Runtime
  folders are rebuilt once.

- NuGet packages (first preview `0.1.0-preview.1`): `WotR.Testing`, a meta-package
  that references the components, and `WotR.Testing.Offline` with the library, its
  build targets (imported automatically, also through `WotR.Testing`), and in
  `tools/` the runner, the snapshot script and `wotr-mono-host.exe`;
  `WotrToolsDirectory` locates them. Built on a maintainer machine with
  `dotnet pack`; a package holding any file outside its allowlist is deleted. Git
  submodules keep working.

- `WotR.Testing.Offline` extracted from the AttributeFeats offline tests: real WotR
  2.7.0 assemblies, blueprint pack, settings and enGB localization; mods loaded
  through their `Info.json` entry method; classified results and `environment.json`
  reports. Available as preview NuGet packages or a Git submodule.
- Tests run on the game's own Unity Mono runtime by default (`-Runtime mono`,
  through `WotR.Testing.MonoHost` and the xUnit v2 console runner); `-Runtime netfx`
  keeps the .NET Framework mode. **Breaking:** projects that used another test
  framework must pass `-Runtime netfx`.
- Dependency mods (`WotrDependencyMod`), loaded in `Requirements`/`LoadAfter` order
  with version checks. `ModEntry` now has `Assembly`, started/active state and a
  per-run copy of the mod folder as `Path`, and is registered with UnityModManager.
- New `runtime-images` boundary: images a mod builds at run time are null.
- On .NET Framework, mod copies have `beforefieldinit` cleared so type initializers
  run in Mono's order; method bodies are unchanged. Projects that asserted the mod
  copy is byte-identical should compare method bodies with `IlComparison` instead.
- `Application` path boundaries return `/`-separated paths, as Unity does.
- Runner: `-MinimumPassed` (default 1; skipped tests do not count), arguments passed
  without quoting problems, old runtime and per-run folders cleaned up.
- `New-WotrSnapshot.ps1` refuses a destination that overlaps the installation and
  includes `MonoBleedingEdge` for the Mono runtime.
- DLC availability is a declared boundary (`dlc-availability`) chosen with `WotrDlc`
  (`all` by default, `none`, `local` or a list); the report shows which DLCs were
  asked about and which mod blueprints are DLC-gated. No store is contacted.
- UnityModManager log lines are captured as written (`umm-log`), written per source
  to `logs/` (gzip above 5 MB) and summarized in `environment.json`; the
  installation's `Log.txt` is never touched.
- Mods shipping different copies of one library no longer fail the run: the first
  copy in load order is used, with a warning that the game may use another copy. The runner
  warns when BlueprintCore is older than the latest release. Warnings never fail a
  run.
- Verification records moved from root `STATUS.md` to the optional private
  `notes/` submodule. Public builds and repository checks do not require it.
- NuGet release workflow: locally built packages are checked again and published
  through GitHub OIDC trusted publishing, without uploading game files or using
  a long-lived API key.
