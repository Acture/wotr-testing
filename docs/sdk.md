# Mod test SDK

The developer SDK for mod authors of Pathfinder: Wrath of the Righteous: a test
project template, pinned dependencies, the game path setup, the way assertions
are written and the way failures are reported.

The tests run the mod against the real game assemblies, the vanilla blueprint
pack and the game's settings, on the game's own Unity Mono runtime. The game is
never started, no save is touched and the installed `Mods` folder is left alone.
The library is described in
[the offline guide](offline.md); this document is the path from "I have a mod" to
"my mechanics are asserted".

## Supported versions

| Part | Version | Status |
| --- | --- | --- |
| Game | 2.7.0 | Verified. Any other version is refused with `[ENV_MISSING]` unless `WOTR_ALLOW_UNVERIFIED_VERSION=1` is set, and the report then records the run as unverified. |
| Mono runtime | The game's own `MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll` (Mono 5.11 in 2.7.0) | Verified. The default runtime. |
| Unity engine | Unknown | The offline runtime never starts the Unity player and uses the installation's own `UnityEngine.CoreModule.dll`. This repository records no verified Unity version; read the engine's version from the installation if you need it. |
| UnityModManager | Unknown | Mods load through the installation's own `UnityModManager.dll`; the SDK pins no UMM version. `environment.json` records the loaded assembly's version (`libraries`). |
| Harmony | The game's 2.0.4 and UnityModManager's copy (2.3.6 in UMM 0.32.4) | Verified with both. A run loads UnityModManager's copy unless `WotrHarmony` says otherwise. |
| .NET Framework | 4.8 (4.8.1 also works), x64 | Verified. Matches the target framework of the mods under test. |
| .NET SDK | 8 or later, on Windows | Verified with 10.0.401. |
| Test framework | xUnit v2 (2.9.2) with `Microsoft.NET.Test.Sdk` 17.11.1 | Verified. Other test adapters on the Mono runtime are not verified; use `WotrRuntime=netfx` for them. |

Everything the SDK needs comes from the author's own installation of the game.
Game files are licensed: never commit them, never upload them, and keep them out
of packages and workflow artifacts.

## Get the template

The template is a test project with every dependency pinned as a
`PackageReference`. Nothing is downloaded, unzipped or copied into the
repository by hand.

```xml
<PackageReference Include="WotR.Testing" Version="[0.1.0-preview.3]" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="[17.11.1]" />
<PackageReference Include="xunit" Version="[2.9.2]" />
<PackageReference Include="xunit.runner.visualstudio" Version="[2.8.2]" />
<PackageReference Include="Xunit.SkippableFact" Version="[1.4.13]" />
```

The direct dependencies are pinned to exact versions, so a restore of this
project picks the same packages every time. `WotR.Testing` is the meta-package:
it brings the offline library with its build targets, the interface fuzzer and
FsCheck.Xunit.

Three ways to get the project:

| Way | How |
| --- | --- |
| Copy the template | Take `templates/WotR.Testing.Sdk/` from the [repository](https://github.com/Acture/wotr-testing) or from the `external/wotr-testing` submodule, rename the files and the namespace, and edit the two settings below. |
| Template package | `dotnet new install WotR.Testing.Sdk --version 0.1.0-preview.3`, then `dotnet new wotr-test -n MyMod`. The package is built from this repository; it is not on a public feed yet (see [distribution boundaries](#distribution-boundaries)). |
| Your own project | Add the same `PackageReference` lines to an existing .NET Framework 4.8 xUnit v2 test project and add the fixture from the template. |

The generated project holds `MyMod.OfflineTests.csproj`, the fixture
`MyModGame.cs`, the example `ExampleTests.cs` and the CI workflow
`ci/offline-tests.yml`.

The template pins the SDK version this repository builds. When a newer preview
is published, change the `WotR.Testing` version in the test project and read the
release notes first: the pinned version is the one the mechanics were verified
with.

## Configure the game path

Two settings in the test project:

| Setting | Meaning |
| --- | --- |
| `WotrModAssembly` | The built mod DLL. Its `Info.json` and its private libraries (for example `BlueprintCore.dll`) are read from the same directory. |
| `WrathInstallDir` | The game directory. Leave it empty and set `WOTR_INPUT_ROOT` instead, or point `WotrSnapshotDirectory` at a snapshot. |

The first match wins: `WotrInputRoot`, then `WOTR_INPUT_ROOT`, then
`WotrSnapshotDirectory` (when it holds a `manifest.json`), then
`WrathInstallDir`/`WrathPath`/`WRATH_PATH`. A missing game directory fails the
build with `[ENV_MISSING] WotR inputs not found`, never with a silent pass.

Keep the local game path out of the repository. On a developer's machine an
untracked `GamePath.props` next to the project is enough:

```xml
<Project>
  <PropertyGroup>
    <WrathInstallDir>C:/Games/Pathfinder Second Adventure</WrathInstallDir>
  </PropertyGroup>
</Project>
```

Other mods the mod under test requires are declared as items and load in the
order their `Info.json` declares:

```xml
<ItemGroup>
  <WotrDependencyMod Include="../OtherMod/bin/Debug/OtherMod.dll" />
</ItemGroup>
```

Build the mod against the same game files the tests use, so two game versions
are never mixed.

## Write the tests

One session per test collection. `WotrGameFixture` starts the real game systems
in the order the game starts them and loads the mod through the entry method
named in its `Info.json`; every test in the collection shares it.

```csharp
public sealed class MyModGame : WotrGameFixture
{
    protected override void VerifyModInitialized(GameSession session)
    {
        if (string.IsNullOrEmpty(session.ModId))
            throw new InvalidOperationException("No mod was loaded. Check WotrModAssembly and the mod's Info.json.");
    }
}

[CollectionDefinition(Name)]
public sealed class MyModGameCollection : ICollectionFixture<MyModGame>
{
    public const string Name = "MyMod offline game";
}
```

`VerifyModInitialized` is optional. Use it to fail the run when the mod did
not report success, for example when it does not write the log line it writes
on a good start. A startup failure is rethrown by `RequireGame()`, so every test
reports the classified cause instead of passing vacuously. Call `RequireGame()`
at the start of each test.

### Synchronous assertions

A synchronous assertion applies the mod's effect and checks the state the game
computed in the same call: a rule's result, a stat snapshot, a buff that is
present or absent.

```csharp
[Fact]
public void MyFeatureChangesTheGameNumbers()
{
    game.RequireGame();
    var feat = OfflineGame.Blueprint<BlueprintFeature>("<the feature blueprint guid>");
    var unit = OfflineGame.CreateUnit(VanillaUnit);
    var before = OfflineGame.StatSnapshot(unit);

    var fact = unit.Progression.Features.AddFeature(feat);

    Assert.True(fact.IsActive, "The feature was not added.");
    Assert.NotEqual(before, OfflineGame.StatSnapshot(unit));   // the effect changed a stat

    unit.Progression.Features.RemoveFact(fact);
    Assert.False(unit.Progression.Features.HasFact(feat));
    Assert.Equal(before, OfflineGame.StatSnapshot(unit));      // and the effect is gone again
}
```

The blueprint, the unit and the stat system are the game's own: the test never
replaces a rule with an expected value.

### Cross-frame (time-dependent) assertions

The offline session has no frame loop. The game's clock does not advance between
two statements of a test, and the SDK does not drive frames. A test that waits
for a frame would assert the same state twice, so a cross-frame assertion is
written in one of these ways:

1. **Assert the time-dependent value the game computed.** The game stores the
   duration of the effect it applied; assert that value.

   ```csharp
   var applied = unit.AddBuff(buff, unit, TimeSpan.FromSeconds(6), null);
   Assert.Equal(6, applied.TimeLeft.TotalSeconds);
   ```

   The same holds for the game's own rule results (a success margin, a number
   of rounds): assert what the rule computed, not what a later frame would
   change.

2. **Assert the mod's own step function.** When the mod's effect depends on the
   number of elapsed ticks, keep the frame-dependent part in the game and make
   the arithmetic a function the test can call with an explicit tick count.
   Assert that function with several tick counts; the game remains the source
   of the timing.

3. **Skip what only the engine can do.** Rendering, scenes, unit views,
   animation, input and the live UI need the Unity player. Catch the failure and
   skip with the SDK's marker, so the result is reported as
   `unity-runtime-required` and never as passed:

   ```csharp
   catch (Exception error) when (UnityNative.IsUnavailable(error))
   {
       game.Observations["myFeature.unityRuntimeRequired"] = error.ToString();
       Skip.If(true, UnityNative.SkipReason("my feature", error));
   }
   ```

   The test needs `[SkippableFact]` for this, which the template already
   references. A skipped test does not count towards the minimum number of
   passing tests.

### Fuzzing an interface

Register the mod's own interface and a factory that returns a fresh instance;
the SDK generates the calls and the arguments, shrinks a failure and saves a
replayable case:

```csharp
[Fact]
public void FuzzMyApi() => Fuzz.Check<IMyModApi>(() => new MyModApi());
```

`game.Fuzz<IMyModApi>(...)` writes the result into the environment report
instead. See [interface fuzzing](fuzzing.md).

### Observations

`game.Observations` is a free-form dictionary that lands in the environment
report. Put the values a failure needs there: the unit's blueprint, the numbers
before and after, the buff that was applied.

## Run the tests locally

```powershell
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj --filter "FullyQualifiedName~MyFeature" --logger trx
dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj --list-tests
```

The package imports the integration automatically: VSTest builds the project,
keeps its own discovery, adapters, filters and loggers and only changes the
runtime provider, so the tests run on the game's Unity Mono runtime. The run
fails when fewer than `WotrMinimumPassed` tests pass (default 1; skipped tests
do not count), when a report is missing, when the startup failed or when the run
timed out (`WotrTestTimeoutSeconds`, default 900).

Useful settings, all optional:

| Setting | Meaning |
| --- | --- |
| `-p:WotrRuntime=netfx` | Run on .NET Framework instead of the game's Mono runtime. Needed for test frameworks other than xUnit v2. The differences are listed in [the offline guide](offline.md#runtimes). |
| `-p:WotrDlc=none` | Which DLCs exist offline: `all` (default), `none`, `local` or a list such as `Dlc4,Dlc6`. No store is contacted. |
| `-p:WotrHarmony=game` | Load the game's own `0Harmony.dll` instead of UnityModManager's copy. |
| `-p:WotrSnapshotDirectory=...` | Use a snapshot instead of the installation. |

## Read a failure

Every run writes a fresh report directory and prints its path. It holds:

| File | Contents |
| --- | --- |
| `environment.json` | The game's version and hashes, the loaded assemblies and their hashes, the startup stages, the environment boundaries the run reached, the DLC policy, the mod's libraries and Harmony copy, the captured log errors and warnings, the mod's log summary and the test observations. |
| `logs/<source>.log` | Every UnityModManager log line, per source. |
| `*.trx` | The standard VSTest report, when `--logger trx` was used. |

The results are classified, and the class tells you where to look:

| Class | Where to look |
| --- | --- |
| `initialization-failed` | `environment.json` → `stages`: the stage that failed and its error. A mod that did not load fails here, not in the test. |
| `environment-missing` | `environment.json` → `inputs`: the game directory, the mod DLL or a dependency mod was not found. |
| `assertion-failed` | The test's own message: the mechanics did not match the expectation. |
| `error` | The exception in the TRX report and in `logErrors`. |
| `unity-runtime-required` | The path reached Unity engine code. It is reported as skipped, never as passed. |
| `skipped-without-reason` | A test skipped without the SDK's marker; the run fails, because the reason is lost. |

Start with `stages` (did the game and the mod start?), then `boundaries` (which
environment adaptations the run reached), then `logErrors` and `modLogs` (what
the mod and the game said), then the test's own message.

## GitHub Actions

`ci/offline-tests.yml` in the template is a workflow for a runner that has the
game installed. Copy it to `.github/workflows/` in the mod's repository and
adapt the paths:

```yaml
jobs:
  offline:
    runs-on: [self-hosted, windows, x64]
    env:
      WOTR_INPUT_ROOT: ${{ vars.WOTR_INPUT_ROOT }}
    steps:
      - uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6
      - uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5
        with:
          dotnet-version: '10.0.x'
          cache: true
          cache-dependency-path: '**/*.csproj'
      - run: dotnet build src/MyMod/MyMod.csproj --configuration Release
      - run: dotnet test tests/MyMod.OfflineTests/MyMod.OfflineTests.csproj --configuration Release --logger trx --results-directory artifacts/test-results
```

GitHub's hosted runners have no game files and cannot run these tests: the game
is licensed and must not enter the repository, a package or a public artifact.
Use a self-hosted Windows runner with the game installed, or restore a snapshot
made with `New-WotrSnapshot.ps1` from a private artifact store. Keep the
environment report on the runner, or upload it as a workflow artifact with an
action pinned by commit SHA.

## Distribution boundaries

These are open, and none of them has a workaround that hides it:

- **The template package is not published yet.** `WotR.Testing.Sdk` is built
  from this repository and verified locally, but no public feed carries it.
  Until the maintainer publishes it, copy `templates/WotR.Testing.Sdk/` from
  the repository or the submodule.
- **The Mono host has no package of its own.** `wotr-mono-host` ships inside
  the `WotR.Testing.Offline` package's `tools/` folder and is started by the
  package's build targets. A standalone host package is unverified and
  unpublished; nothing in the SDK needs one, so none was built.
- **In-game testing does not exist yet.** Paths that need the Unity engine are
  skipped as `unity-runtime-required`; running tests inside the game is
  planned.
- **Public CI runs only the game-independent tests.** This repository's own
  workflow runs the fuzzing library's tests, which need no game files.

The known limits of the offline environment (no engine, no save, no scene, the
starting equipment of pregens, the loaders that are supported) are listed in
[the offline guide](offline.md#known-limits).
