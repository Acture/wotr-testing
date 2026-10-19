# WotR.Testing.Offline

Runs a Pathfinder: Wrath of the Righteous mod against the real game assemblies,
vanilla blueprint pack and settings on the game's own Unity Mono runtime (or .NET
Framework), without starting `Wrath.exe` or the Unity player. The mod is loaded through the entry method
in its `Info.json`, and its blueprints are created by its own patches on the
game's `BlueprintsCache.Init`.

Status: preview; the API may change. Verified with WotR 2.7.0, AttributeFeats
(BlueprintCore) and TabletopTweaks-Base with TabletopTweaks-Core as a dependency
mod. See [docs/offline.md](https://github.com/Acture/wotr-testing/blob/main/docs/offline.md) for how it works, every environment adaptation
and the known limits.

The library and its NuGet package contain no game files. Game assemblies are
referenced at compile time from your own installation and copied and rewritten on
the test machine. License: AGPL-3.0 with an
[additional permission](https://github.com/Acture/wotr-testing/blob/main/LICENSE-EXCEPTION.md)
for use together with the game and Unity assemblies.

## Use it in a mod's test project

Requirements: Windows, .NET SDK 8 or later, a UnityModManager mod with
`Info.json` in its build output, and the game installed (or a snapshot from
`New-WotrSnapshot.ps1`).

### From NuGet

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <WotrModAssembly>../MyMod/bin/Debug/MyMod.dll</WotrModAssembly>
    <WrathInstallDir>C:/Games/Pathfinder Second Adventure</WrathInstallDir>
  </PropertyGroup>
  <ItemGroup>
    <!-- Optional: mods named in Info.json Requirements, loaded first. -->
    <WotrDependencyMod Include="../deps/TabletopTweaks-Core/TabletopTweaks-Core.dll" />
    <PackageReference Include="WotR.Testing" Version="0.1.0-preview.3" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

`WotR.Testing` is the meta-package for all WotR.Testing components; referencing
`WotR.Testing.Offline` directly works the same. The build targets are imported
automatically. Run the test project directly:

```powershell
dotnet test tests/MyMod.Tests/MyMod.Tests.csproj
dotnet test tests/MyMod.Tests/MyMod.Tests.csproj --filter "FullyQualifiedName~MyFeat" --logger trx
```

### From a Git submodule

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <WotrModAssembly>../MyMod/bin/Debug/MyMod.dll</WotrModAssembly>
    <WrathInstallDir>C:/Games/Pathfinder Second Adventure</WrathInstallDir>
  </PropertyGroup>
  <Import Project="../../external/wotr-testing/src/WotR.Testing.Offline/build/WotR.Testing.Offline.targets" />
  <ItemGroup>
    <!-- Optional: mods named in Info.json Requirements, loaded first. -->
    <WotrDependencyMod Include="../deps/TabletopTweaks-Core/TabletopTweaks-Core.dll" />
    <ProjectReference Include="../../external/wotr-testing/src/WotR.Testing.Offline/WotR.Testing.Offline.csproj" AdditionalProperties="WotrInputRoot=$(WotrInputRoot)" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

Use `dotnet test` with either installation method. It runs the existing VSTest host
and xUnit v2 adapter on the game's own Mono runtime by default; test discovery,
Theory rows, filters and loggers keep their normal behavior. AppDomain isolation
and shadow copying are disabled automatically for Mono. `--settings` merges the
user's settings and preserves their supported paths and environment variables.
Use `-p:WotrRuntime=netfx` for .NET Framework; other test frameworks on Mono are not
yet verified. Build the mod without deploying it to the game.

Register a mod interface and a fresh-instance factory with
`game.Fuzz<IMyApi>(() => new MyApi())`. The library automatically generates method
calls and arguments, shrinks unexpected failures and saves exact replay cases in
the environment report. `WotR.Testing.Fuzzing` is included by this component;
the meta-package also includes FsCheck.Xunit for author-written `[Property]`
tests. See [interface fuzzing](https://github.com/Acture/wotr-testing/blob/main/docs/fuzzing.md)
for custom generators, replay and per-sequence state cleanup.

Every execution writes fresh environment reports, checks that their reported
runtime matches the selected runtime and fails on zero tests, missing reports,
initialization failure, timeouts, unexplained skips, or fewer than
`WotrMinimumPassed` passing tests. `--list-tests` only discovers tests.
The report directory is printed after a successful VSTest run; a native VSTest
failure already returns a nonzero exit code. PowerShell 7 is only required for the
optional snapshot script and the previous runner, which remain in `tools/` for
compatibility (`WotrToolsDirectory` locates them).

```csharp
public sealed class MyModGame : WotrGameFixture
{
    // Optional: fail initialization unless the mod reports success.
    protected override void VerifyModInitialized(GameSession session) { }
}

[CollectionDefinition("game")]
public sealed class GameCollection : ICollectionFixture<MyModGame> { }

[Collection("game")]
public sealed class MyFeatTests
{
    private readonly MyModGame game;
    public MyFeatTests(MyModGame game) => this.game = game;

    [Fact]
    public void FeatAddsBonus()
    {
        game.RequireGame();
        var unit = OfflineGame.CreateUnit("<vanilla unit blueprint guid>");
        var before = OfflineGame.StatSnapshot(unit);
        unit.Progression.Features.AddFeature(OfflineGame.Blueprint<BlueprintFeature>("<feat guid>"));
        // assert on unit.Stats, then remove the fact and compare with before
    }
}
```

Game types can be used anywhere in test classes. The targets generate a module
initializer into the test assembly that installs the game assembly resolver; the
first request for a game assembly, even while the test framework discovers tests,
prepares the runtime folder. If your project already defines
`System.Runtime.CompilerServices.ModuleInitializerAttribute`, set
`WotrGenerateModuleInitializer=false` and call `OfflineRuntime.InstallResolver()`
from your own module initializer.

Catch `UnityNative.IsUnavailable(error)` and skip with `UnityNative.SkipReason(...)`
when a path reaches Unity engine code; such results are reported as
`unity-runtime-required`, never as passed.

## Build properties and variables

| Name | Purpose |
|---|---|
| `WotrRuntime` | `mono` (default, the game's Unity Mono through VSTest) or `netfx` |
| `WotrTestTimeoutSeconds` | VSTest session timeout (default 900); an explicit `TestSessionTimeout` in user runsettings takes precedence |
| `WotrMinimumPassed` | Minimum passing tests (default 1); skips do not count |
| `WotrModAssembly` | Built mod DLL (required). `Info.json` and private DLLs such as `BlueprintCore.dll` are read from its directory |
| `WotrDependencyMod` (items) | Other built mods loaded with it, ordered by `Requirements`/`LoadAfter` |
| `WotrXunitConsoleVersion` | xUnit v2 console runner for `-Runtime mono` (default 2.9.2), downloaded with `PackageDownload` |
| `WotrInputRoot` / `WOTR_INPUT_ROOT` | Game root; otherwise `WotrSnapshotDirectory` (with `manifest.json`), then `WrathInstallDir`/`WrathPath`/`WRATH_PATH` |
| `WotrWorkDirectory` | Rewritten runtime cache and per-run state (default `obj/wotr/`) |
| `WotrReportDirectory` | Base for fresh per-run environment reports; otherwise `--results-directory`/`wotr/<project>`, then `wotr-reports` beside the test assembly. `dotnet test` sets `WOTR_OFFLINE_REPORT_DIR` for its test process |
| `WOTR_ALLOW_UNVERIFIED_VERSION=1` | Run on a game version not yet verified (reported as unverified) |
| `WotrDlc` / `WOTR_DLC` | Available DLCs: `all` (default), `none`, `local` (installed in the Steam library) or names such as `Dlc4,Dlc6` |
| `WOTR_NO_UPDATE_CHECK=1` | Skip the BlueprintCore release check (also `-NoUpdateCheck` on the runner) |
| `WotrHarmony` / `WOTR_HARMONY` | The `0Harmony.dll` the run loads: `umm` (default, UnityModManager's copy), `game` (`Wrath_Data/Managed`) or a path; the runner's `-Harmony` also takes a `Lib.Harmony` version such as `2.2.2` |
| `WotrHarmonyReference` (items) / `WOTR_HARMONY_REFERENCES` | Harmony copies the mods are compared with (default `game`); members they lack are warned. The runner's `-HarmonyReference` also takes versions |
| `WotrTestMode` | `auto` (default: every environment the assembly declares), `offgame` or `ingame` ([in-game testing](https://github.com/Acture/wotr-testing/blob/main/docs/ingame.md#run)) |
| `WotrTestKind` | `all` (default), `tests` or `fuzz` (registered `[WotrFuzz]` targets) |
| `WotrFuzzSeed` / `WotrFuzzReplay` | One seed for every fuzz target / a saved case to replay exactly |
| `WotrInGameConnection`, `WotrInGamePid` | `auto` (default), `attach` or `launch`; the game to attach to |
| `WotrInGameLaunchInBackground` | `true` (default): a game the run starts gives the focus back while it loads |
| `WotrInGameTestTimeoutSeconds`, `WotrInGameStartupTimeoutSeconds` | Per in-game case or target (default 60); game start and loading (default 300) |
| `WotrInGameMod` (items) | Builds the in-game host requires loaded (default `WotrModAssembly` and `WotrDependencyMod`) |
| `WotrTestingPlatform` | `true` when `global.json` selects Microsoft.Testing.Platform (detected); `dotnet test` then starts the WotR test application with `--mode`, `--tests`, `--fuzz`, `--fuzz-seed` and `--fuzz-replay` |

## API

| Type | Use |
|---|---|
| `WotrGameFixture` | Boots the session once; `RequireGame()`, `Observations`, `Session`; writes `environment.json` on dispose |
| `GameSession` | Startup stages, loaded `Mods` in load order, mod under test's log, entry path and assembly |
| `OfflineGame` | `Blueprint<T>(guid)`, `CreateUnit(guid)`, `StatSnapshot(unit)` |
| `UnityNative` | Recognize and report paths that need the Unity engine |
| `IlComparison` | Prove that rewritten game and mod copies keep every method body |
| `OfflineRuntime` | `IsUnityMono`/`RuntimeKind`, runtime folder, inputs and rewrite counts |
| `Boundaries` | Registry of environment adaptations and their hit counts |
