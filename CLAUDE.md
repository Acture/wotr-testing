# wotr-testing development

Test tooling for Pathfinder: Wrath of the Righteous mods. Public repository,
AGPL-3.0. Read [README.md](README.md),
[docs/README.md](docs/README.md) and [docs/offline.md](docs/offline.md) before
changing behavior. CLAUDE.md is the single real instruction file: `AGENTS.md` points
to it and `.github/copilot-instructions.md` points to `../CLAUDE.md`.

## Layout

- `src/WotR.Testing/`: `WotR.Testing` meta-package (no code; depends on the
  components). `src/WotR.Testing.Offline/`: offline library, its `build/*.targets`
  and README.
- `src/WotR.Testing.Fuzzing/`: game-independent automatic interface fuzzer;
  `tests/WotR.Testing.Fuzzing.Tests/` runs on .NET 8 without game inputs.
- `src/WotR.Testing.MonoHost/`: `wotr-mono-host`, starts the game's own Unity Mono
  runtime (the default test runtime) from the inputs.
  `src/WotR.Testing.VSTest/`, `src/WotR.Testing.VSTestBootstrap/` and
  `src/WotR.Testing.Build/` integrate it with `dotnet test`, isolate SDK host
  dependencies and validate reports. These tools contain no game files.
- In progress, not packaged: `src/WotR.Testing.InGame/` (in-game testing; so far the
  Unity-independent main-thread dispatcher) and `src/WotR.Testing.InGame.Protocol/`
  (adapter/host messages, framing, session checks);
  `tests/WotR.Testing.InGame.Tests/` runs both without game inputs. Planned: the UMM
  host, external adapter and meta-package wiring.
- `scripts/`: `Invoke-WotrOfflineTests.ps1` (generic runner) and
  `New-WotrSnapshot.ps1` (game-file snapshot). Consumers call these paths through
  the submodule; moving them is a breaking change.
- `tests/`: repository checks, CLI integration tests and small synthetic fixtures
  (`tests/fixtures/`) for the native runtime and FsCheck.
- `docs/`: public documentation. `data/`: no datasets; game files never go there.
- `.github/`: CI, pull request template and CODEOWNERS.
- `notes/`: optional private notes submodule; public builds and checks must not
  require it.
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
- Opt-in runner integration tests: restore `tests/fixtures/MonoTests/MonoTests.csproj`,
  and `tests/fixtures/FuzzTests/FuzzTests.csproj` for FsCheck integration,
  set `WOTR_TEST_INTEGRATION=1`, then run the Python suite above. They need local
  game inputs and skip in public CI. Verify real game behavior through a consumer: the
  AttributeFeats repository (`external/wotr-testing` submodule) runs
  `pwsh -NoProfile -File scripts/Invoke-OfflineMechanicsTests.ps1`, currently 10/10
  on the default Unity Mono runtime. Changes to mod loading should also be checked
  with a mod that has a dependency mod (TabletopTweaks-Base with -Core).
  Point its submodule at a local branch to test before publishing. Do not claim
  untested behavior has passed.
- Run `dotnet test tests/WotR.Testing.Fuzzing.Tests/WotR.Testing.Fuzzing.Tests.csproj`
  for game-independent fuzzing tests and
  `dotnet test tests/WotR.Testing.InGame.Tests/WotR.Testing.InGame.Tests.csproj` for
  the in-game protocol and dispatcher (also net48 on Windows). Public CI runs these;
  building the offline library still needs game files. They do not show that
  anything works inside the player.
- Use CLI and code; do not use computer use or launch the game for offline work.

## Changes

- Verified game version: 2.7.0.
- Write verified outcomes and their limits to `notes/STATUS.md` when the private
  notes submodule is available; user-visible changes,
  breaking changes and migration steps to `CHANGELOG.md`.
- Focused branches and concrete diffs. Do not add AI co-author lines. Obtain
  authorization for commits, pushes, PRs, history rewrites and branch deletion.
