# wotr-testing development

Test tooling for Pathfinder: Wrath of the Righteous mods. Public repository,
AGPL-3.0. Read [README.md](README.md), [STATUS.md](STATUS.md),
[docs/README.md](docs/README.md) and [docs/offline.md](docs/offline.md) before
changing behavior. CLAUDE.md is the single real instruction file: `AGENTS.md` points
to it and `.github/copilot-instructions.md` points to `../CLAUDE.md`.

## Layout

- `src/WotR.Testing.Offline/`: offline library, its `build/*.targets` and README.
- `src/WotR.Testing.MonoHost/`: `wotr-mono-host`, starts the game's own Unity Mono
  runtime (the default test runtime) from the inputs.
  Planned: `src/WotR.Testing.InGame/` for in-game testing, and a shared
  `WotR.Testing` layer once both need it.
- `scripts/`: `Invoke-WotrOfflineTests.ps1` (generic runner) and
  `New-WotrSnapshot.ps1` (game-file snapshot). Consumers call these paths through
  the submodule; moving them is a breaking change.
- `tests/`: repository checks and their tests; library behavior tests and small
  synthetic fixtures (`tests/fixtures/`) once they exist.
- `docs/`: public documentation. `data/`: no datasets; game files never go there.
- `.github/`: CI, pull request template and CODEOWNERS.
- Build outputs go to ignored `artifacts/`. Keep the local `GamePath.props`
  (`WrathInstallDir`) at the root and untracked.

## Game files

- Never commit, package, upload or publish game DLLs, bundles, localization files
  or snapshots of them, including in issues, PRs, CI artifacts or test reports.
  `tests/check_repository.py` rejects tracked game files.
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

- Toolchain: Windows, .NET SDK, PowerShell 7. `dotnet build WotR.Testing.slnx`
  needs a WotR installation (`GamePath.props`).
- Repository checks (Python 3.11+ and uv; CI runs them on every push):
  ```powershell
  python tests/check_repository.py
  python -m unittest discover -s tests -p 'test_*.py'
  uvx ruff==0.16.10 check --config .ruff.toml tests
  uvx ruff==0.16.10 format --check --config .ruff.toml tests
  uvx ty==0.0.82 check tests
  ```
- The library has no tests of its own yet. Verify changes through a consumer: the
  AttributeFeats repository (`external/wotr-testing` submodule) runs
  `pwsh -NoProfile -File scripts/Invoke-OfflineMechanicsTests.ps1`, currently 10/10
  on the default Unity Mono runtime. Changes to mod loading should also be checked
  with a mod that has a dependency mod (TabletopTweaks-Base with -Core).
  Point its submodule at a local branch to test before publishing. Do not claim
  untested behavior has passed.
- Public CI has no game files and cannot build this repository yet (OSS-358).
- Use CLI and code; do not use computer use or launch the game for offline work.

## Tracking and changes

- Linear project `wotr-testing` under initiative "WotR 模组开发" (team Open Source)
  owns tasks, dependencies and decisions; this repository owns implementation and
  evidence. Next work: OSS-358 (try a second, structurally different mod, then
  NuGet and a `dotnet new` template), related OSS-48, OSS-61, OSS-26 (in-game
  host), OSS-23 (headless launch). Verified game version: 2.7.0.
- Write verified outcomes and their limits to `STATUS.md`; user-visible changes,
  breaking changes and migration steps to `CHANGELOG.md`.
- Focused branches and concrete diffs. Do not add AI co-author lines. Obtain
  authorization for commits, pushes, PRs, history rewrites and branch deletion.
