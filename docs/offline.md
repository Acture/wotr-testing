# Offline testing

`WotR.Testing.Offline` runs a mod against the real Pathfinder: Wrath of the
Righteous assemblies and blueprint data in a test process, on either the game's own
Unity Mono runtime or .NET Framework (see [Runtimes](#runtimes)). It does not start
`Wrath.exe`, the Unity player or the Unity Editor, and it does not touch saves or
the installed `Mods` folder.

It shows that real game rules apply a mod's blueprints to real units. It cannot
show anything that needs the Unity engine: rendering, scenes, unit views,
animation, input or the live UI.

## What runs

| Part | Source |
|---|---|
| Game code | `Assembly-CSharp`, `Assembly-CSharp-firstpass`, Owlcat and Unity managed assemblies from the inputs |
| Vanilla blueprints | `Bundles/blueprints-pack.bbp`, read by the game's own `BlueprintsCache` |
| Settings and other ScriptableObjects | `Bundles/blueprint.assets`, mapped from the Unity type tree onto the game's types |
| Localization | `Wrath_Data/StreamingAssets/Localization/enGB.json`, loaded by the game's `LocalizationManager` |
| Mod | The built mod DLL and its private dependencies (for example `BlueprintCore.dll`), loaded through the `Info.json` entry method; its blueprints come from its own patches on `BlueprintsCache.Init` |
| Dependency mods | Other built mods declared with `WotrDependencyMod`, for example a framework mod named in `Requirements`, loaded the same way |
| Units | Vanilla unit blueprints, built by the game's `UnitEntityData` constructor; class levels come from the game's `AddClassLevels` |

Startup runs these stages in order and records each one: `runtime`, `harmony`,
`application-paths`, `type-cache`, `asset-list`, `unity-services`, `settings`,
`localization`, `mod-load`, `blueprints` and, when the test project defines one,
`mod-verification`.

## Loading mods

Mods load as UnityModManager loads them. The mod under test and every
`WotrDependencyMod` are ordered by their `Info.json` `Requirements` and `LoadAfter`;
a requirement that is not declared, or older than its `Id-Version` minimum, is
reported as `[ENV_MISSING]`. Each mod gets its own `ModEntry` with `Assembly` set,
started, active and registered in `UnityModManager.modEntries`. `ModEntry.Path` is a
per-run copy of the mod's build folder, so the mod reads its own `Assets`,
`Localization` and settings files and may write settings without touching the build
output; with no user settings there, mods use their defaults. All mods and their
private libraries share one process, as in the game, so two different copies of one
library are rejected.

## Inputs

- Windows, PowerShell 7 and the .NET SDK. Tests target .NET Framework 4.8 or
  4.8.1 (match the mods under test), x64.
- For the default Unity Mono runtime: the game's `MonoBleedingEdge` folder in the
  inputs, an xUnit v2 test project, and the .NET Framework 4.8 runtime that ships
  with Windows. Nothing else is installed.
- One copy of the game files. The first match wins:
  1. `WotrInputRoot` (`-WotrInputRoot` on the runner or `/p:WotrInputRoot=`);
  2. the `WOTR_INPUT_ROOT` environment variable;
  3. `WotrSnapshotDirectory`, if it contains `manifest.json`;
  4. `WrathInstallDir` (or `WrathPath`/`WRATH_PATH`).

Build the mod against the same inputs so game versions are never mixed. Only
verified game versions run (currently 2.7.0). Any other version is reported as
`[ENV_MISSING]` unless `WOTR_ALLOW_UNVERIFIED_VERSION=1` is set, in which case the
report records it as unverified.

To pin a copy for a workspace or a controlled CI runner:

```powershell
pwsh -NoProfile -File scripts/New-WotrSnapshot.ps1 -WrathInstallDir "<game directory>" -Destination vendor/wotr
```

This copies the managed assemblies, the two bundles, `Version.info` and the
English and sound localization files (about 330 MB) and writes `manifest.json`
with the game version and SHA-256 hashes. A run refuses a snapshot whose version
or key hashes do not match its manifest. These are licensed game files: keep them
out of Git, packages and public artifacts.

## Running

```powershell
pwsh -NoProfile -File scripts/Invoke-WotrOfflineTests.ps1 -Project path/to/MyMod.OfflineTests.csproj
```

The runner builds the project, runs the tests with a hang timeout (default 15
minutes) and writes the reports below. A run in which fewer than `-MinimumPassed`
tests pass (default 1; skipped tests do not count) fails.

| File | Contents |
|---|---|
| `wotr-offline.trx` (`netfx`), `wotr-offline.xunit.xml` (`mono`) | Test report |
| `environment.json` | Process, input versions and hashes, loaded assembly paths and hashes, startup stages, boundary hit counts, asset gaps, captured game log errors and warnings, mod log and test observations |
| `summary.json`, `summary.md` | Classified results, runtime, source commit, problems |

Every result is classified:

| Class | Meaning | Fails the run |
|---|---|---|
| `passed` | The assertions held | No |
| `environment-missing` | Inputs or the mod build were not found (`[ENV_MISSING]`) | Yes |
| `initialization-failed` | A real game or mod startup stage failed (`[INIT_FAILED] <stage>`) | Yes |
| `assertion-failed` | The mechanics did not match the expectation | Yes |
| `error` | Any other exception | Yes |
| `unity-runtime-required` | The path reached Unity native code (`[UNITY_RUNTIME_REQUIRED]`); reported as skipped, never as passed | No |
| `skipped-without-reason` | Skipped without that marker | Yes |

The run also fails on zero tests, a missing test report or `environment.json`, or
a timeout. A failed startup is reported by every test with its stage name; nothing
is retried with substitutes.

## Runtimes

| `-Runtime` | Process | Use |
|---|---|---|
| `mono` (default) | The game's own Unity Mono runtime (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`, Mono 5.11 in 2.7.0) with the game's class libraries, started by `WotR.Testing.MonoHost` and the xUnit v2 console runner | Matches how the game runs mod code |
| `netfx` | .NET Framework 4.8 through `dotnet test` | Any test framework. Differs from the game where noted below |

`WotR.Testing.MonoHost` (`src/WotR.Testing.MonoHost`, a small .NET Framework program
the runner builds with `dotnet build`) loads the Mono library from the declared
inputs and calls its exported `mono_main`, the entry point of `mono.exe`; the tests
then run entirely on the game's runtime, and the host process only starts it.
Nothing is copied out of the installation. Its class library
folder contains the game's framework assemblies (`mscorlib`, `System*`, `Mono.*`,
`netstandard`) plus the type-forwarding facades of the local .NET Framework 4.8
installation, which Unity does not ship but test frameworks reference. Game, Unity
and mod assemblies are absent from it, so they load from the rewritten runtime
folder. `environment.json` records `process.runtimeKind` (`unity-mono` or `netfx`).
The game's Mono library is the one player module allowed in the test process, and
only from the declared inputs; `UnityPlayer.dll` and `Wrath.exe` never are.

Differences on .NET Framework, which do not apply on `mono`:

- **Member access.** Mods compiled against a publicized `Assembly-CSharp` need the
  widening below; Unity Mono does not check member access.
- **Type initializer order.** .NET Framework runs a `beforefieldinit` type's static
  initializer at the first static field read, Unity Mono before the type's first
  static method call. A mod that saves a game delegate in a static field and then
  replaces the game's delegate (TabletopTweaks-Core does this for armor class
  filters) would capture its own replacement and recurse. On .NET Framework the mod
  copies therefore have `beforefieldinit` cleared; their method bodies are unchanged
  (`rewrite.modTypesWithPreciseInitialization` counts the types, and
  `IlComparison.CompareMethodBodies(original, copy)` proves the bodies).
- **Harmony patches on generic types.** Harmony cannot patch a method of a generic
  type instantiation on .NET Framework (`NotImplementedException` from its detour).
  The mod then fails at `[INIT_FAILED] mod-load`; nothing is skipped. Use `mono`.

## How the game runs without Unity

On either runtime, any method that calls a Unity engine function implemented in
native code has no implementation outside the player. The library therefore builds
a runtime folder under `WotrWorkDirectory` from copies of the inputs. The originals
are never modified. The copies differ in two ways:

1. **Unity internal calls get managed bodies.** By default they throw
   `NotSupportedException("UNITY_NATIVE_UNAVAILABLE: <method>")`, so code that
   needs the engine fails visibly. Four narrow rules return instead:
   - telemetry calls (`UnityEngine.Analytics*`, crash reporting) return defaults;
   - native release calls reached from finalizers do nothing;
   - instance setters on native objects (which cannot exist offline) drop the write;
   - native allocators (`Internal_Create*`, `Init`) return a null handle.

   Reads from those objects still throw.
2. **Members the mod side uses become public (.NET Framework only).** BlueprintCore
   and many mods are compiled against a publicized `Assembly-CSharp`. Mono does not
   check member access; .NET Framework does. Only the types, fields and non-virtual methods
   that the mod and its game-referencing dependencies actually reference are
   widened. A widened field that the game's blueprint serializer did not read
   before is marked `NotSerialized`, so the serialized field set stays the same.

`IlComparison.CompareMethodBodies` compares a rewritten assembly with its original
instruction by instruction, so a test project can prove that game method bodies
are unchanged. The rewrite counts are written to `environment.json` (`rewrite`).

A module initializer generated into the test assembly installs the assembly
resolver. The first request for a game assembly, even during test discovery,
prepares the folder, so test classes can use game types anywhere.

## Declared boundaries

Each replacement below is registered with its hit count in `environment.json`.
None of them changes game rules such as bonuses, targeting, buff lifetimes or
rulebook calculations.

| Boundary | Replaces | Offline behaviour |
|---|---|---|
| `unity-paths` | `Application.dataPath`, `streamingAssetsPath`, `persistentDataPath`, `temporaryCachePath` | Inputs; persistent and cache paths under the work directory |
| `unity-platform` | `Application.platform/isEditor/isPlaying`, `SystemInfo.systemMemorySize`, `Debug.isDebugBuild` | A playing Windows release player with 16 GB |
| `unity-log`, `owlcat-log` | Unity's native log sink and Owlcat's logger | Captured into the report |
| `shader-ids` | `Shader.PropertyToID` | Stable integer per name (visual static constructors) |
| `scene-objects` | `Object.FindObjectsOfType`, `Resources.FindObjectsOfTypeAll` | Empty: no scene is loaded |
| `bug-report-service` | `ReportingUtils` constructor | Skipped; it would contact the developer's report server |
| `graphics-settings` | `GraphicsSettingsController` constructor, graphics quality autodetection | Skipped |
| `audio-service`, `character-atlas-service` | `SoundState`, `CharacterAtlasService` | Registered without running constructors that create GameObjects |
| `referenced-assets` | `BlueprintReferencedAssets.Get` during blueprint loading | Real entries; ScriptableObjects mapped from `blueprint.assets`, native assets (sprites, textures, prefabs, meshes) null |
| `settings-asset` | `SettingsValues` | Mapped from the real asset, including difficulty presets |
| `runtime-images` | `Texture2D` constructors, `ImageConversion.LoadImage`, `Sprite.Create` | Images a mod builds at run time (icons from PNG files) have no native texture: constructors are skipped, `LoadImage` returns false and `Sprite.Create` returns null, like native assets |

The game language setting is set to English before the game's own
`LocalizationManager.Init`, as a player choosing English would; without it the
game would ask Unity for the system language.

## Known limits

- **Runtime.** `mono` uses the game's runtime and class libraries but no Unity
  player; `netfx` adds the differences listed under [Runtimes](#runtimes). Either
  can hide a failure that needs the engine. Confirm important results in the game.
- **No engine.** Unit views, scenes, physics, pathing, animation and rendering do
  not exist. Spawning through `EntityCreationController` needs a view prefab; use
  pre-made units instead, for example the pre-made path of `RuleSummonUnit`.
- **Starting equipment.** Pregens log "can't insert item ... to slot" for their
  starting weapons and armor, so they are unarmed and unarmored. The cause has not
  been established.
- **Creature types.** The game gives undead and constructs no Constitution; pick
  test units whose rules match what is asserted.
- **Loaders.** Only UnityModManager mods are supported, alone or with declared
  dependency mods. OwlcatModification mods are not.
- **DLC.** The game's DLC check reaches Steam, fails offline and is logged as an
  error; DLC content is treated as unavailable. This is not yet a declared boundary.
- **Mod log.** The report reads UnityModManager's bounded log history after
  startup; a verbose mod can push out an earlier mod's lines.
- **Player state.** No save is loaded, so code that needs `Game.Instance.Player`
  (for example `PrerequisitePlayerHasFeature`) logs errors when units are created.
- **CI.** Public CI has no game files, so it cannot build or run these tests.
