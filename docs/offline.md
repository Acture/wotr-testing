# Offline testing

`WotR.Testing.Offline` runs a mod against the real Pathfinder: Wrath of the
Righteous assemblies and blueprint data in an ordinary .NET Framework test
process. It does not start `Wrath.exe`, the Unity player or the Unity Editor, and
it does not touch saves or the installed `Mods` folder.

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
| Units | Vanilla unit blueprints, built by the game's `UnitEntityData` constructor; class levels come from the game's `AddClassLevels` |

Startup runs these stages in order and records each one: `runtime`, `harmony`,
`application-paths`, `type-cache`, `asset-list`, `unity-services`, `settings`,
`localization`, `mod-load`, `blueprints` and, when the test project defines one,
`mod-verification`.

## Inputs

- Windows, PowerShell 7 and the .NET SDK. Tests target .NET Framework 4.8, x64.
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
minutes) and writes:

| File | Contents |
|---|---|
| `wotr-offline.trx` | Standard test report |
| `environment.json` | Process, input versions and hashes, loaded assembly paths and hashes, startup stages, boundary hit counts, asset gaps, captured game log errors and warnings, mod log and test observations |
| `summary.json`, `summary.md` | Classified results, source commit, problems |

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

The run also fails on zero tests, a missing TRX or `environment.json`, or a
timeout. A failed startup is reported by every test with its stage name; nothing
is retried with substitutes.

## How the game runs without Unity

The game normally runs on Unity's Mono runtime. On .NET Framework, any method that
calls a Unity engine function implemented in native code cannot even be compiled.
The library therefore builds a runtime folder under `WotrWorkDirectory` from
copies of the inputs. The originals are never modified. The copies differ in two
ways:

1. **Unity internal calls get managed bodies.** By default they throw
   `NotSupportedException("UNITY_NATIVE_UNAVAILABLE: <method>")`, so code that
   needs the engine fails visibly. Four narrow rules return instead:
   - telemetry calls (`UnityEngine.Analytics*`, crash reporting) return defaults;
   - native release calls reached from finalizers do nothing;
   - instance setters on native objects (which cannot exist offline) drop the write;
   - native allocators (`Internal_Create*`, `Init`) return a null handle.

   Reads from those objects still throw.
2. **Members the mod side uses become public.** BlueprintCore and many mods are
   compiled against a publicized `Assembly-CSharp`. Mono does not check member
   access; .NET Framework does. Only the types, fields and non-virtual methods
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

The game language setting is set to English before the game's own
`LocalizationManager.Init`, as a player choosing English would; without it the
game would ask Unity for the system language.

## Known limits

- **Runtime.** Tests run on .NET Framework 4.8, not Unity's Mono, with the
  rewritten Unity copies above. A runtime difference can hide a failure the game
  would show, or cause one it would not. Confirm important results in the game.
- **No engine.** Unit views, scenes, physics, pathing, animation and rendering do
  not exist. Spawning through `EntityCreationController` needs a view prefab; use
  pre-made units instead, for example the pre-made path of `RuleSummonUnit`.
- **Starting equipment.** Pregens log "can't insert item ... to slot" for their
  starting weapons and armor, so they are unarmed and unarmored. The cause has not
  been established.
- **Creature types.** The game gives undead and constructs no Constitution; pick
  test units whose rules match what is asserted.
- **Loaders.** Only UnityModManager mods are supported. OwlcatModification mods
  and loading several mods together are not.
- **CI.** Public CI has no game files, so it cannot build or run these tests.
