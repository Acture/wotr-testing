# wotr-testing

Test tooling for Pathfinder: Wrath of the Righteous mods.

| Component | Status |
|---|---|
| [`WotR.Testing.Offline`](src/WotR.Testing.Offline/README.md) | Runs a UnityModManager mod against the real game assemblies, vanilla blueprint pack and settings in a .NET Framework test process, without starting the game or Unity |
| In-game testing | Planned: running tests inside the game for paths that need the Unity engine |

The repository contains no game files. Game assemblies are referenced at compile
time from your own installation, and the offline runtime is prepared on the test
machine. Never commit, package or publish game files or snapshots of them.

Status: early. Verified with WotR 2.7.0 and the
[AttributeFeats](https://github.com/Acture/attribute-feats) mod; not yet published
as a NuGet package and the API may change. Consume it as a Git submodule for now.

## Quick start

```powershell
git submodule add https://github.com/Acture/wotr-testing.git external/wotr-testing
```

Create a .NET Framework 4.8 xUnit project, import
`external/wotr-testing/src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets`,
reference `WotR.Testing.Offline.csproj` and set `WotrModAssembly` to your built mod.
See the [library README](src/WotR.Testing.Offline/README.md) for a complete project
and the API, and [docs/offline.md](docs/offline.md) for how it works, every
environment adaptation and the known limits.

```powershell
pwsh -NoProfile -File external/wotr-testing/scripts/Invoke-WotrOfflineTests.ps1 -Project tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
```

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

## License

[GNU Affero General Public License v3.0](LICENSE), with an
[additional permission](LICENSE-EXCEPTION.md) to link and convey it together with
the proprietary game and Unity engine assemblies.
