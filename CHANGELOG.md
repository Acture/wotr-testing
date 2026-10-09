# Changelog

## Unreleased

- `WotR.Testing.Offline` extracted from the AttributeFeats offline tests: real WotR
  2.7.0 assemblies, blueprint pack, settings and enGB localization; mods loaded
  through their `Info.json` entry method; classified results and `environment.json`
  reports. Consumed as a Git submodule; no package has been released.
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
- Mods shipping different copies of one library no longer fail the run: the copy
  loaded first in load order is used, as in the game, with a warning. The runner
  warns when BlueprintCore is older than the latest release. Warnings never fail a
  run.
