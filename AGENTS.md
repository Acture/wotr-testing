# wotr-testing development

Test tooling for Pathfinder: Wrath of the Righteous mods. Public repository,
AGPL-3.0. Read [README.md](README.md) and [docs/offline.md](docs/offline.md) first.

## Layout

- `src/WotR.Testing.Offline/`: offline library, its `build/*.targets` and README.
- `scripts/`: `Invoke-WotrOfflineTests.ps1` (generic runner) and
  `New-WotrSnapshot.ps1` (game-file snapshot).
- `docs/`: public documentation. Planned: `src/WotR.Testing.InGame/` for in-game
  testing, and a shared `WotR.Testing` layer once both need it.
- Build outputs go to ignored `artifacts/`. Keep the local `GamePath.props`
  (`WrathInstallDir`) at the root and untracked.

## Game files

- Never commit, package, upload or publish game DLLs, bundles, localization files
  or snapshots of them, including in issues, PRs, CI artifacts or test reports.
- Game assemblies are compile-time references only; the offline runtime folder is
  generated on the test machine.

## Offline testing rules

- Adapt only at the environment edge (Unity native calls, paths, logging,
  telemetry, graphics, audio, scene lookups, assets). Never replace game rules,
  bonuses, targeting, buff lifetimes or rulebook calculations with expected values.
- Register every adaptation in `Boundaries` with a hit count and document it in
  `docs/offline.md`. Remove boundaries that are no longer reached.
- Game method bodies in rewritten assemblies must stay unchanged; widened fields
  must not change the blueprint serializer's field set.
- Paths that reach Unity native code are `unity-runtime-required` (skipped), never
  passed. Zero tests, missing reports, timeouts and startup failures fail the run.
- Keep the runtime differences (.NET Framework vs Unity Mono, rewritten Unity
  copies) visible in documentation and reports.

## Verification

- `dotnet build WotR.Testing.slnx` needs a WotR installation (`GamePath.props`).
- The library has no tests of its own yet. Verify changes through a consumer: the
  AttributeFeats repository (`external/wotr-testing` submodule) runs
  `pwsh -NoProfile -File scripts/Invoke-OfflineMechanicsTests.ps1`, currently 9/9.
  Point its submodule at a local branch to test before publishing.
- Public CI has no game files and cannot build this repository yet (OSS-358).
- Use CLI and code; do not use computer use or launch the game for offline work.

## Tracking

Linear project `wotr-testing` under initiative "WotR 模组开发" (team Open Source).
Next work: OSS-358 (try a second, structurally different mod, then NuGet and a
`dotnet new` template), related OSS-48, OSS-61, OSS-26 (in-game host), OSS-23
(headless launch). Verified game version: 2.7.0.
