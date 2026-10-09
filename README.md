# wotr-testing

Test tooling for Pathfinder: Wrath of the Righteous mods.

| Component | Status |
|---|---|
| [`WotR.Testing.Offline`](src/WotR.Testing.Offline/README.md) | Runs UnityModManager mods against the real game assemblies, vanilla blueprint pack and settings on the game's own Unity Mono runtime (or .NET Framework), without starting the game or the Unity player |
| In-game testing | Planned: running tests inside the game for paths that need the Unity engine |

The repository contains no game files. Game assemblies are referenced at compile
time from your own installation, and the offline runtime is prepared on the test
machine. Never commit, package or publish game files or snapshots of them.

Status: early. Verified with WotR 2.7.0, the
[AttributeFeats](https://github.com/Acture/attribute-feats) mod (BlueprintCore) and
TabletopTweaks-Base with TabletopTweaks-Core as a dependency mod; not yet published
as a NuGet package and the API may change. Consume it as a Git submodule for now.

## Quick start

```powershell
git submodule add https://github.com/Acture/wotr-testing.git external/wotr-testing
```

Create a .NET Framework 4.8 (or 4.8.1) xUnit v2 project, import
`external/wotr-testing/src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets`,
reference `WotR.Testing.Offline.csproj`, set `WotrModAssembly` to your built mod and
add a `WotrDependencyMod` item for each mod it requires.
See the [library README](src/WotR.Testing.Offline/README.md) for a complete project
and the API, and [docs/offline.md](docs/offline.md) for how it works, every
environment adaptation and the known limits.

```powershell
pwsh -NoProfile -File external/wotr-testing/scripts/Invoke-WotrOfflineTests.ps1 -Project tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
```

The tests run on the game's own Mono runtime by default; `-Runtime netfx` uses .NET
Framework instead, with the differences listed in
[docs/offline.md](docs/offline.md#runtimes).

## Building

Requires Windows, the .NET SDK and a WotR installation. Set `WrathInstallDir` with
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

Outputs go to the ignored `artifacts/` directory. Public CI cannot build this
repository because it has no game files.

## Repository layout

| Path | Owns |
| --- | --- |
| `src/` | Libraries and their MSBuild targets ([overview](src/README.md)) |
| `scripts/` | Test runner and game-file snapshot scripts, called by consumers through the submodule |
| `tests/` | Repository checks; library tests and synthetic fixtures once they exist |
| `docs/` | Public documentation ([index](docs/README.md)) |
| `data/` | No datasets; game files never belong in this repository |
| `.github/` | CI, pull request template and CODEOWNERS |
| [`STATUS.md`](STATUS.md) | Verified behavior and its limits |
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
