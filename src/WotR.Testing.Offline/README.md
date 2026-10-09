# WotR.Testing.Offline

Runs a Pathfinder: Wrath of the Righteous mod against the real game assemblies,
vanilla blueprint pack and settings in an ordinary .NET Framework test process,
without starting `Wrath.exe` or Unity. The mod is loaded through the entry method
in its `Info.json`, and its blueprints are created by its own patches on the
game's `BlueprintsCache.Init`.

Status: not yet published as a package; the API may change. Verified with WotR
2.7.0 and one mod. See [docs/offline.md](../../docs/offline.md) for how it works,
every environment adaptation and the known limits.

The library contains no game files. Game assemblies are referenced at compile
time and copied and rewritten on the test machine.

## Use it in a mod's test project

Requirements: Windows, the .NET SDK, a UnityModManager mod with `Info.json` in its
build output, and the game installed (or a snapshot from
`scripts/New-WotrSnapshot.ps1`).

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
    <ProjectReference Include="../../external/wotr-testing/src/WotR.Testing.Offline/WotR.Testing.Offline.csproj" AdditionalProperties="WotrInputRoot=$(WotrInputRoot)" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

Add `xunit.runner.json` with `"appDomain": "denied"` and `"shadowCopy": false`,
copied to the output. Build the mod without deploying it to the game.

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
| `WotrModAssembly` | Built mod DLL (required). `Info.json` and private DLLs such as `BlueprintCore.dll` are read from its directory |
| `WotrInputRoot` / `WOTR_INPUT_ROOT` | Game root; otherwise `WotrSnapshotDirectory` (with `manifest.json`), then `WrathInstallDir`/`WrathPath`/`WRATH_PATH` |
| `WotrWorkDirectory` | Rewritten runtime cache and per-run state (default `obj/wotr/`) |
| `WotrReportDirectory` / `WOTR_OFFLINE_REPORT_DIR` | Where `environment.json` is written |
| `WOTR_ALLOW_UNVERIFIED_VERSION=1` | Run on a game version not yet verified (reported as unverified) |

## API

| Type | Use |
|---|---|
| `WotrGameFixture` | Boots the session once; `RequireGame()`, `Observations`, `Session`; writes `environment.json` on dispose |
| `GameSession` | Startup stages, mod log, mod entry path and assembly |
| `OfflineGame` | `Blueprint<T>(guid)`, `CreateUnit(guid)`, `StatSnapshot(unit)` |
| `UnityNative` | Recognize and report paths that need the Unity engine |
| `IlComparison` | Prove that rewritten game assemblies keep every method body |
| `Boundaries` | Registry of environment adaptations and their hit counts |
