# wotr-testing

Test tooling for Pathfinder: Wrath of the Righteous mods.

| Component | Status |
|---|---|
| [`WotR.Testing.Offline`](src/WotR.Testing.Offline/README.md) | Runs UnityModManager mods against the real game assemblies, vanilla blueprint pack and settings on the game's own Unity Mono runtime (or .NET Framework), without starting the game or the Unity player |
| [`WotR.Testing.Fuzzing`](src/WotR.Testing.Fuzzing/README.md) | Automatically generates method calls and argument values for interfaces selected by a mod author, shrinks failures and replays saved cases |
| In-game testing | Planned: running tests inside the game for paths that need the Unity engine |

The repository contains no game files. Game assemblies are referenced at compile
time from your own installation, and the offline runtime is prepared on the test
machine. Never commit, package or publish game files or snapshots of them.

Status: early. Verified with WotR 2.7.0, the
[AttributeFeats](https://github.com/Acture/attribute-feats) mod (BlueprintCore) and
TabletopTweaks-Base with TabletopTweaks-Core as a dependency mod. The NuGet package
is a preview and the API may change.

## Quick start

Create a .NET Framework 4.8 (or 4.8.1) xUnit v2 project, add the `WotR.Testing`
NuGet package (preview), set `WotrModAssembly` to your built mod and add a
`WotrDependencyMod` item for each mod it requires. `WotR.Testing` references the
components (`WotR.Testing.Offline`, which carries the build targets, test runner
and Mono host, and `WotR.Testing.Fuzzing`); the packages contain no game files.

```powershell
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
```

To follow unreleased changes, use this repository as a Git submodule instead: import
`external/wotr-testing/src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets`,
reference `WotR.Testing.Offline.csproj` and use the same `dotnet test` command. See the
[library README](src/WotR.Testing.Offline/README.md) for complete projects and the
API, and [docs/offline.md](docs/offline.md) for how it works, every environment
adaptation and the known limits.

The tests run on the game's own Mono runtime by default. Standard `--list-tests`,
`--filter`, `--settings`, `--no-build` and `--logger trx` work through VSTest and the
test framework's adapter. `-p:WotrRuntime=netfx` uses .NET Framework instead, with the differences listed in
[docs/offline.md](docs/offline.md#runtimes).
The previous PowerShell runner remains available for existing consumers.

Mod authors can register an interface and factory to fuzz their API automatically:

```csharp
[Fact]
public void FuzzMyApi() => Fuzz.Check<IMyModApi>(() => new MyModApi());
```

`WotR.Testing.Fuzzing.Fuzz` discovers the selected interface's methods, generates
arguments and call sequences, shrinks failures and saves replayable cases. Use
`game.Fuzz<IMyModApi>(...)` to include the results in the offline environment
report. See [interface fuzzing](docs/fuzzing.md) for custom generators and limits.
The meta-package also includes FsCheck.Xunit for author-written `[Property]` tests.

## Building

Requires Windows, .NET SDK 8 or later and a WotR installation. Set `WrathInstallDir` with
`/p:WrathInstallDir=...`, the `WrathPath`/`WRATH_PATH` environment variable, or an
ignored `GamePath.props` at the repository root:

```xml
<Project>
  <PropertyGroup>
    <WrathInstallDir>C:/Games/Pathfinder Second Adventure</WrathInstallDir>
  </PropertyGroup>
</Project>
```

```powershell
dotnet build WotR.Testing.slnx
```

Outputs go to the ignored `artifacts/` directory. Public CI runs the independent
fuzzing library's tests; building the offline library needs game files.

## Repository layout

| Path | Owns |
| --- | --- |
| `src/` | Libraries and their MSBuild targets ([overview](src/README.md)) |
| `scripts/` | Test runner and game-file snapshot scripts, called by consumers through the submodule |
| `tests/` | Repository checks and synthetic fixtures for native runtime and FsCheck integration |
| `docs/` | Public documentation ([index](docs/README.md)) |
| `data/` | No datasets; game files never belong in this repository |
| `.github/` | CI, pull request template and CODEOWNERS |
| `notes/` | Optional private verification records; not required to build or test |
| [`CHANGELOG.md`](CHANGELOG.md) | User-visible changes |

Report bugs and request features in GitHub Issues; see [contributing](CONTRIBUTING.md)
and [security reporting](SECURITY.md). Development rules are in [CLAUDE.md](CLAUDE.md).

## Repository checks

CI runs these on every push and pull request; they need Python 3.11+ and uv, not
the game:

```powershell
python tests/check_repository.py
python -m unittest discover -s tests -p 'test_*.py'
uvx ruff==0.16.10 check --config .ruff.toml tests
uvx ruff==0.16.10 format --check --config .ruff.toml tests
uvx ty==0.0.82 check tests
```

They check required files, public links, instruction aliases and that no game files
or local game paths are tracked. They do not build or test the library.

## License

[GNU Affero General Public License v3.0](LICENSE), with an
[additional permission](LICENSE-EXCEPTION.md) to link and convey it together with
the proprietary game and Unity engine assemblies. The repository scaffolding
(checks, CI and contribution files) is adapted from Acture's open-source project
template, MIT License, Copyright (c) 2026 Acture.
