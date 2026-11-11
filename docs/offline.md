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

Mod authors can register an interface and a factory with
`game.Fuzz<IMyModApi>(() => new MyModApi())`. The library generates method calls
and parameters, shrinks failures and records replayable cases in
`environment.json.observations`. Sequences reaching Unity native code are counted
as unavailable, and an all-unavailable run fails. See [interface fuzzing](fuzzing.md)
for generators, replay and the lifecycle of game state across samples.

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
output; with no user settings there, mods use their defaults.

All mods and their private libraries share one process, as in the game. When mods
ship different copies of one library (for example BlueprintCore 2.8.6 and 2.8.7),
the game's Mono uses the copy loaded first for every mod, whatever version each mod
was built against (verified with two mods carrying different versions of one
library). A library loads when code first uses it, not when its mod loads, so which
copy the game uses is not predictable from the mod list. A run uses the first copy
in load order and reports a warning naming every copy; `environment.json` lists the
copy of each library used (`libraries`). The run's result may differ with another
copy.

## DLC

The game asks the store (Steam, GOG, Epic, ...) whether each DLC is owned, which
needs the player. Offline the test project chooses instead, with `WotrDlc` (or
`WOTR_DLC` at run time):

| `WotrDlc` | Available DLCs |
|---|---|
| `all` (default) | Every DLC; the same on every machine |
| `none` | None |
| `local` | DLCs whose depots the Steam library holding the inputs has installed (`appmanifest_*.acf`); DLCs without their own depot, such as pre-order rewards, count as unavailable |
| `Dlc4,Dlc6` | The listed DLC blueprint names (`Dlc1`...`Dlc6`, `DlcCommanderPack`, `FreeDlc1`, ...) |

The choice applies for the whole run, because mods usually check DLCs while they
create blueprints. `environment.json` (`dlc`) records the policy, every DLC the game
or a mod asked about with the answer and count, listed names that were never asked
about, and the blueprints mods added or replaced that carry a `DlcCondition`, with
the DLC reward that gates them. Mods that change DLC-gated vanilla blueprints in
place are visible only through the DLCs they ask about.

## Inputs

- Windows and .NET SDK 8 or later. Tests target .NET Framework 4.8 or
  4.8.1 (match the mods under test), x64.
- For the default Unity Mono runtime: the game's `MonoBleedingEdge` folder in the
  inputs, an xUnit v2 test project and its VSTest adapter, and the .NET Framework 4.8 runtime that ships
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

This copies the managed assemblies, the two blueprint bundles, `Version.info`, the
English and sound localization files, and the navigation graphs that battles with an
area use (`Bundles/locationlist.json` and `Bundles/*.nav`, about 95 MB), about
430 MB in all. It does not copy the mechanics scene bundles (`Bundles/*.scenes`,
about 1.9 GB of mechanics scenes), from which battles need only the settings of
A*: it writes those to `navigation-settings.json`, the serialized fields of the
`AstarPath` component of each scene whose bundle name contains `mechanic`, without
its graph data, with each bundle's SHA-256, and also lists the scenes without an
`AstarPath`. In 2.7.0 every area part with a navigation graph names such a scene.
The script builds `src/WotR.Testing.Snapshot` to read them, so it needs the .NET
SDK; the tool checks that the file gives the settings back exactly and fails the
snapshot otherwise. It does not copy the unit view prefabs (`Bundles/*.unit`, about
7.5 GB): battles read a unit's prefab on demand when the inputs have it, as with the
installation, and otherwise need a corpulence override or fallback for the unit's
size (see [Battles](#battles)). It writes `manifest.json` with the game version and
SHA-256 hashes; a battle records the hash of each bundle it reads. A run refuses a
snapshot whose version or key hashes do not match its manifest. These are licensed
game files: keep them out of Git, packages and public artifacts.

## Running

```powershell
dotnet test path/to/MyMod.OfflineTests.csproj
dotnet test path/to/MyMod.OfflineTests.csproj --filter "FullyQualifiedName~MyFeat" --logger trx
dotnet test path/to/MyMod.OfflineTests.csproj --list-tests
```

The package imports the integration automatically. VSTest builds the project and
uses the existing test adapter, with a session timeout of 15 minutes by default
(`WotrTestTimeoutSeconds`, or an explicit `TestSessionTimeout` in user runsettings).
`--filter`, `--settings`, `--no-build`, `--results-directory` and standard loggers
retain their ordinary VSTest behavior. A run in which fewer than
`WotrMinimumPassed` tests pass (default 1; skipped tests do not count) fails.
Discovery with `--list-tests` does not require execution reports.

Each execution gets a fresh environment-report directory, printed at the end of
the run, beneath `WotrReportDirectory` when set. Otherwise it is beneath
`--results-directory`/`wotr/<project>` when supplied, then `wotr-reports` beside the
test assembly. The test process receives this path as `WOTR_OFFLINE_REPORT_DIR`;
reports left by an earlier run cannot satisfy the current run's checks.

On Mono, a managed entry point in `wotr-mono-host` installs the offline assembly
resolver in the consumer's AppDomain before invoking the SDK testhost. This also
covers game-typed fields inspected during test discovery, before a module
initializer would run. The SDK's method instructions remain unchanged. The
bootstrap path and hash are recorded in `environment.json.testHost.bootstrap`.

| File | Contents |
|---|---|
| `*.trx` (with `--logger trx`), or another selected VSTest logger | Standard test report on either runtime |
| `environment.json` | Process, input versions and hashes, loaded assembly paths and hashes, VSTest host dependency isolation, mod libraries used, startup stages, boundary hit counts, DLC policy and use, asset gaps, captured game log errors and warnings, a summary of each mod's log, warnings and test observations |
| `logs/<source>.log` | Every UnityModManager log line per source (`Manager` or a mod Id); gzip-compressed (`.log.gz`) above 5 MB |
| `wotr-results.xml` under `WotrWorkDirectory/test-runs/<run-id>/` | Native VSTest outcomes and classifications used by the post-run validation |

The previous `Invoke-WotrOfflineTests.ps1` entry point is retained for existing
consumers and requires PowerShell 7. It still writes `wotr-offline.trx` (`netfx`) or
`wotr-offline.xunit.xml` (`mono`), and `summary.json`/`summary.md`. NuGet consumers
use `dotnet test` directly; no script lookup is needed.

Each mod log summary in `environment.json` gives the line count, the error lines and
the first and last 20 lines, with consecutive repeats collapsed into one line and a
count.

Warnings never fail a run. They cover differences a player's game may have: mods
shipping different copies of one library and Harmony members that mods reference
but a reference Harmony copy lacks (see
[Harmony copies](#harmony-copies)).
The previous PowerShell runner also checks BlueprintCore's latest NuGet release
(`WW-Blueprint-Core`; skipped with `-NoUpdateCheck` or `WOTR_NO_UPDATE_CHECK=1`,
and when BlueprintCore is merged into the mod assembly).

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

The run also fails on zero tests, a missing result-logger report or `environment.json`,
a timeout, or an environment report naming a different runtime. An initialization
error in `environment.json` fails even if a test forgot to call `RequireGame()`.
A failed startup is reported by every test with its stage name; nothing
is retried with substitutes.

## Battles

`OfflineBattle` builds a battle from a `BattleScenario` and can rebuild the same
initial state. The scenario lists units on each side by `BlueprintUnit` guid and
what to change on them; the battle applies every change through the game's own
API and checks it afterwards. A unit's changes are applied by kind, in the order
of this table; changes of one kind in the order they were added:

| Scenario entry | Game API | Checked |
|---|---|---|
| Unit | `UnitEntityData` constructor from the blueprint (body, starting items, `AddClassLevels`) | Errors the game logs while building fail the build |
| `WithFaction` | `UnitDescriptor.SwitchFactions` after the unit is built | Faction |
| `WithClassLevels` | `AddClassLevels.LevelUp` with the game's default selections | Class level; a blueprint's `ClassLevelLimit` turns levels into a level-up plan and fails the build |
| `WithBaseStat` | `ModifiableValue.BaseValue` | Base value |
| `Equip` | `ItemSlot.InsertItem` | The slot holds the item; a refused item fails the build with the game's reason |
| `WithFact`, `WithAbility`, `WithBuff` | `AddFact`, `AddBuff` (the unit as caster, duration in rounds) | The fact, ability or buff is active |
| `WithResource` | `Restore` and `Spend` on a resource the unit has | Current amount |
| `At` | `UnitEntityData.Position` | Nothing else |
| `MainCharacter` | `Player.SetMainCharacter` for a player-faction unit | `GameHelper.GetPlayerCharacter()` |
| `Area` | `AreaPersistentState` as the loaded area, its navigation graph, the game loop (see below) | The area has a navigation graph |
| `CorpulenceOverrideBySize` | Corpulence in meters by original size that replaces the view prefab's (see below) | Positive values |
| `CorpulenceFallbackBySize` | Corpulence in meters by original size, used only when the inputs lack the unit's view prefab (see below) | Positive values |

```csharp
// Commands need an area: frames run on its navigation graph. KenabresBurning, with positions on its graph.
var scenario = new BattleScenario("duel") { MainCharacter = "fighter", Area = "92180b58582ec5f43a756071cd339f52" };
scenario.Unit("fighter", "party", "<player-faction unit guid>").Equip(BattleSlot.PrimaryHand, "<weapon guid>").At(50, 40, -70);
scenario.Unit("cultist", "enemies", "<enemy unit guid>").WithResource("<resource guid>", 1).At(52, 40, -70);

using var battle = OfflineBattle.Start(scenario, seed: 3);
var fighter = battle.Unit("fighter");
// A rule directly...
var attack = Rulebook.Trigger(new RuleAttackWithWeapon(fighter, battle.Unit("cultist"), fighter.Body.PrimaryHand.Weapon, 0));
// ...or a command through the game's command loop.
fighter.Commands.Run(new UnitAttack(battle.Unit("cultist")));
battle.RunUntilIdle(TimeSpan.FromSeconds(12));
battle.Reset();   // same scenario and seed: battle.InitialState equals the first build's
```

Battle setup runs inside `RuleScope`: an error the game logs and catches while the
units are built fails the build, unless `SetupLogAllowances` explains it with a
reason. Nothing is retried or replaced. A failed build cleans up what it created
before `BattleSetupException` is thrown.

**Player context.** The battle prepares the party inventory with
`Player.EnsureInventory()` once per process: player-faction units without their
own inventory keep their starting items there. It also creates the player's party
formation (`Player.FormationManager`) once, as the game does on first use; the
game's click handlers read it when the player moves or attacks. Rules that consult the player
character need `MainCharacter`; for example `RuleDealDamage` from a party unit to a
non-party unit calls `GameHelper.IsAttackingGreenNPC`, which reads the player
character's group. Without one, that rule logs the game's NullReferenceException
and `RuleScope` fails the operation. The main character must be a player-faction
unit that uses the party inventory.

**Area and game loop.** Set `BattleScenario.Area` to a `BlueprintArea` guid to run
the game's own loop. The battle then loads the area as the game's current area
(`AreaPersistentState`, no scene) and records it as the scene loader's current area
and area part (`Game.CurrentlyLoadedArea`), as `SceneLoader.LoadAreaCoroutine`
does without an enter point; code such as the game's buff ticking reads it. It
also loads the area's navigation graph as the game's `NavMeshManager` does: the
graph asset named by the area's `GraphCache`, found through
`Bundles/locationlist.json` in its `.nav` bundle and deserialized by the game's A*
code. A* takes the serialized settings of the `AstarPath` in the area's mechanics
scene (`BlueprintAreaPart.DynamicScene`), read on demand from its `.scenes`
bundle: nearest-node search, heuristic and path-processing limits. KenabresBurning,
for example, searches every graph for the nearest node, where the class default
searches only the closest graph. A snapshot has no scene bundles; it keeps these
settings of every mechanics scene in `navigation-settings.json` (see
[Inputs](#inputs)), and A* takes them from there. Without either, A* keeps its
class defaults and the report warns; `battles.navigation` records the source and
the values. A* limits its work per frame by the computer's clock: the scene's
`maxFrameTime` of searching (10 ms in KenabresBurning), after which a search or the
rest of the queue waits for the next frame, and 0.5 ms of handing finished paths
back. A battle frame searches every path requested before A* runs in it and hands
all of them back, as a machine fast enough for both limits does, so the frame a path
finishes in follows the game's order, not the machine's speed or load (boundary
`pathfinding-clock`; `battles.navigation` keeps the scene's value as
`sceneMaxFrameTime`). Units that use the party inventory go into
`Player.CrossSceneState`, the others into the area's main state, and all are in the
game (`IsInGame`). Each unit gets a view stand-in (boundary `unit-view-stand-in`): a
`UnitEntityView` with no GameObject, model or animator that Unity's checks read as
live. It is made from the view prefab `UnitEntityData.CreateView` would instantiate
(an active polymorph's, else the custom prefab or the blueprint's `Prefab`, else the
game's legacy-unit override), read on demand from its `.unit` bundle: as Unity's
`Instantiate` does, it copies the serialized fields of the prefab's
`UnitEntityView` (corpulence, rotation lock), `OverrideSizeScale` and
`UnitMovementAgent` (acceleration, speeds, avoidance). So a unit's corpulence,
which reach, engagement, attacks of opportunity, spacing and vision use, is its
model's, scaled by size as the game scales it. References to models and colliders
stay null; `environment.json` lists each prefab under `battles.unitViews` with its
bundle hash, serialized corpulence and the fields left unset.

A scenario can configure corpulence by a unit's original size (for a polymorph's
view, the unit's size at that moment) in two separate ways:

| Setting | With the unit's bundle in the inputs | Without it (for example a snapshot) |
|---|---|---|
| `CorpulenceOverrideBySize[Size.Huge] = 2f` | The prefab is read; only its corpulence is replaced, its movement agent and size override apply | The override is the corpulence; the view and movement agent keep their class defaults |
| `CorpulenceFallbackBySize[Size.Huge] = 2f` | Not used: the prefab's own corpulence applies | The fallback is the corpulence; the view and movement agent keep their class defaults |
| Neither | The prefab's corpulence | The build fails as environment-missing |

The game still scales a configured value when the unit's size changes, as it scales
the prefab's (a polymorph's view uses it unscaled, as the game does for polymorph
views). The class defaults differ from many prefabs, for example an acceleration of
20 where the Glabrezu's prefab has 8, so movement timing differs without the
bundle. Overrides give the same corpulence with and without the bundles; fallbacks
let a battle run without them but can give other results than the installation.
`battles.unitViews` records each view's source: `prefab`,
`prefab, corpulence override`, `corpulence override (...)` or
`corpulence fallback (...)`.

The game's `AttachView` attaches the view, so fact, part and view-attached hooks
run; only the view's own side of attaching, which sets up models, is replaced. The
view carries the game's own `UnitMovementAgent` with the seeker and path modifiers
its `Init` creates. A polymorph swaps the view for one of its own prefab, with size
scaling off and every command but movement interrupted, and swaps it back when it
ends, as the game's `Polymorph` does. Units get these views in battles with and
without an area. Give units commands with the game's API, for example
`unit.Commands.Run(new UnitAttack(target))`, then run frames:

| Call | Does |
|---|---|
| `battle.RunUntilIdle(limit)` | Runs frames until no battle unit has a command left and returns the game time that took; throws `TimeoutException` when commands still run after `limit` |
| `battle.Run(duration)` | Runs frames for a fixed span of game time |

**Game mode.** A battle with an area runs the game's default mode: the controllers
`GameModesFactory.Create(GameModeType.Default)` builds, in the game's order, except
the presentation controllers listed below. The battle starts the mode as
`Game.DoStartMode` does: it is pushed on the game's mode stack, so
`Game.CurrentMode` is `Default` and `Game.GetController` finds its controllers,
then started and activated, and the public controllers the mechanics use (hand
equipment, combat engagement) are set from it. Cleanup stops it as `DoStopMode`
does (boundary `game-mode`). A frame starts as a Unity frame does: Unity's frame
count, real time and time advance by 1/30 s (boundary `frame-clock`), then Unity's
`Update` of the A* component runs (path requests), then what `Game.Tick` runs for
the mechanics: the actions the game scheduled, the real-time clock, the mode's own
`GameMode.Tick` and the temporary entities. The game's own `TimeController` sets the
time scale and game time.

So everything the default mode does for the mechanics runs. Units walk paths the
game's A* finds on the area's navigation mesh, avoid each other and accelerate as
the game's agent makes them. They see each other within vision range, remember
enemies, join combat and roll initiative (`UnitCombatPrepareController`), and a
unit surprised into combat waits for its initiative before it acts or makes attacks
of opportunity. The game's AI (`AiBrainController`) chooses actions for units in
combat, except units of controlled sides (below). Commands approach, start, act and
end in the game's code; abilities run through `AbilityExecutionController`,
projectiles fly and area effects tick. Leaving an enemy's reach disengages and
provokes an attack of opportunity. Buffs count down and end. A unit whose damage
reaches its hit points falls unconscious or dies as `UnitLifeController` decides,
which also takes it out of combat, interrupts its commands, gives experience and
raises the game's death events. Visual effects, sounds, highlights and the view's
side of a death are skipped (boundaries `fx`, `sound`, `ability-cast-fx`,
`unit-highlight`, `unit-death-visuals`). If the game asks to switch to another mode
(a dialog, a cutscene, the pause), the frame fails with the mode named.

The presentation controllers a battle leaves out:

| Controller | Why |
|---|---|
| `KeyboardAccess`, `PointerController` | Keyboard and mouse input |
| `UnpauseController`, `AutoPauseController` | The player's pause |
| `InteractionHighlightController`, `InspectUnitsController`, `TutorialController`, `SelectionCharacterController`, `GameLogController` | UI windows, highlights, selection and the combat log |
| `FogOfWarController` | Fog of war, rendered on the GPU |
| `FxSpawnController`, `FootprintsController`, `WeatherController` | Visual effects, decals and weather |
| `UnitAnimationController` | Animations; there is no animator, so commands act on their first tick |
| `CameraController`, `EntitiesInCameraFrustumController` | The camera |
| `UnitPlaceOnGroundController` | Raycasts against level colliders, which battles do not load |
| `GameOverController` | Ending the session with the game-over screen when the party falls; a battle is decided by its consumer |
| `ClockworkController`, `DungeonLeaderboardController`, `DlcSaveImporterController` | QA automation, the online leaderboard and save import |

**Turn-based battles.** `BattleScenario.TurnBased` switches the game's own
turn-based setting (`SettingsRoot.Game.TurnBased.EnableTurnBasedMode`) on for the
battle, as a player does, and restores it at cleanup. As in the game, combat becomes
turn-based when the party enters it, so the scenario needs an `Area` and a
`MainCharacter`. The game's `CombatController` then orders the units by initiative,
runs the surprise round and the rounds after it, and its `TurnController` lets one
unit act at a time: the game's AI acts on its units' turns and ends them, and a
party unit's turn waits for the player's commands. Out of combat the game runs in
real time. What the turn-based combat reads of the game's interface runs without a
screen: the camera does not scroll to the unit whose turn starts, and the pointer,
the path preview, the selection manager and the cursor are the game's own without
their drawing (boundaries `camera`, `pointer`, `path-preview`, `selection-ui`,
`cursor`, `input`).

**Controlled sides.** `BattleScenario.ControlledSides` names the sides whose units
act only on the commands given to them: the game's AI never decides for them, not
even the actions it takes for party members whose AI the player switched off
(boundary `external-control`). Rules that force actions, such as confusion, fear
or attacks of opportunity, still apply. Every other unit in combat acts on the
game's AI, as in the game, where the party's own AI follows
`UnitEntityData.IsAIEnabled`. A test that gives every command itself controls all
sides; with the game's AI on a side, units in combat keep fighting, so
`RunUntilIdle` returns only when nobody acts on their own. The key state shows each
unit's control (`control controlled` or `control game-ai`).

Frames run inside `RuleScope`: an
error the game logs and catches, for example an exception in an event handler,
fails the call. Errors the game's own code logs in normal play are allowed, each
with the code path that logs it: a touch spell's held charge, which
`AbilityEffectStickyTouch` adds as `UnitPartTouch`, is subscribed to EventBus by
`EntityPart.TurnOn` and again by `UnitPartTouch.Init`, and the game's
`SubscriptionManager` logs "Can't subscribe on events twice" and keeps one
subscription. A scenario allows further logs it explains in
`BattleScenario.FrameLogAllowances`, as `SetupLogAllowances` does for the build;
`battles.allowedFrameLogs` counts the allowed logs by reason. A frame in which the game sets its time scale to 0, which pauses
the game (`PlayerTimeScale` 0, for example), fails the call with
`InvalidOperationException` instead of running forever. Limits count frame time:
the `TimeController.DeltaTime` each frame advances the game's controllers by. In
real time the game's clock advances by the same amount; in turn-based combat it
stands still during a turn, as in the game. Without an area, battles
build as before and frames refuse to run. See the [known limits](#known-limits).

**Reset.** `Reset()` and `Dispose()` run in the test process. Cleanup unsubscribes
handlers added with `battle.Subscribe`, then removes each unit as the game's
`EntityDestructionController` does: it destroys and disposes the unit, whose
disposal leaves combat with the view still attached and then destroys the view
object, which destroys the stand-in; it removes the unit from its scene state and
the awake units, and raises the destruction event. It then puts back the previous main character, stops the
game loop, updates the player's combat state, unloads the navigation graph and the
area, removes and disposes items the build put into the party inventory, and removes
blueprints added with `battle.AddBlueprint`. Finally it restores game time (changed
by frames and `battle.AdvanceGameTime`), the frame clock, and the game RNG from
before the build. An error the game logs and catches during cleanup, for example in
a destruction handler, counts as a failed cleanup step.

It then compares the game-wide state with the state before the build: EventBus
global subscribers and every subscriber under each unit, the same for Rulebook
subscriptions, `EntityService` entities, the game's unit and awake-unit lists,
`Player.CrossSceneState`, view stand-ins, party inventory items, their counts and
the inventory's identity, game time and frame clock, the player's combat state,
party and main character, unit groups, the loaded area and the scene loader's
current area, `AstarPath.active`,
`Game.Instance.LevelUpController`, the turn-based controller's current turn, the
rulebook event stack, the game RNG seed and call count, and the temporary
blueprints. `battle.LastCleanup` lists failed
steps and anything left behind. The game's `Game.Instance.DefaultUnit` and the items
it holds are not counted: the game creates it the first time a calculation needs a
unit and has none, and keeps it for the session.

A cleanup that leaves anything marks the process dirty
(`BattleWorker.IsDirty`). The process then refuses new battles with
`[BATTLE_WORKER_DIRTY]` and the run fails validation, because it can no longer
give a clean environment; run further battles in a new process. `Dispose()` never
throws, so an exception from the test body is never hidden; `Reset()` throws
`BattleCleanupException`. Only one battle is active per process, because the state
it changes is process-wide. A battle still active when the run ends was never
disposed, so its state was never cleaned up: the worker counts as dirty and the run
fails validation as well. A new worker process is not started automatically.

`battle.InitialState` and `battle.Observe()` give the key state as sorted text
lines: per unit its blueprint, side, faction, levels, hit points, every stat, each
equipment slot, own inventory, buffs, facts, abilities, resources, position,
whether a command is running and whether the unit is in combat, plus the party
inventory, main character, party size, game time and game RNG. Unique ids are left out. Building a unit makes no
game RNG calls, so another seed changes only the RNG line of `InitialState`.

**Paths.** `BattlePathSpec` keeps a path that already exists, with its id,
waypoints including both endpoints, the distance or cost the source supplied, their
units and the source (`FromGamePoints` copies `Pathfinding.Path.vectorPath` from a
path the game computed). `battle.Paths` maps its `From` and `To` unit ids to the
units of the current build, also after `Reset()`. The battle never computes,
repairs or measures these paths. Units that move during frames follow paths the
game's own A* finds on the area's navigation mesh, not the scenario's paths.

`environment.json` records `battles`: the reset method, whether the worker is dirty
and why, battles started, builds, cleanups, the player context prepared, the state
compared and the last cleanups with their residue, and per navigation graph its
bundle and hash and where A* took its settings from.

## Battle environment

`BattleEnvironment` is a decision loop over a battle for an external decision
maker, for example a training program. It needs an area, because every step runs
game frames, and at least one controlled side: the decision maker commands the
units of `BattleScenario.ControlledSides` and the game's AI decides for every other
unit (see [controlled sides](#battles)). Control the party to play as the player;
control every side for self-play, with one policy per side. The rules stay the
game's: the environment decides when frames stop and what it reports, and gives
units the game's own commands.

```csharp
using var environment = BattleEnvironment.Start(scenario, seed: 1, new BattleEnvironmentOptions { MaxSteps = 150 });
var observation = environment.Observe();          // episode 1; Reset() starts the next one
while (true)
{
    var actions = policy(observation);            // chosen from observation.Candidates
    var result = environment.Step(actions);
    if (result.Ended) break;                       // decided (Outcome) or truncated at MaxSteps
    observation = result.Observation;
}
var record = environment.Record;                   // seed, versions, actions and key state per step
var differences = environment.Replay(record);      // empty when the episode reproduces
```

| Call | Does |
|---|---|
| `BattleEnvironment.Start(scenario, seed, options)` | Builds the scenario with `OfflineBattle` as episode 1 |
| `Reset(seed)` | Cleans up the episode and builds the scenario again, as `OfflineBattle.Reset` does; returns the first observation |
| `Observe()` | The units, the paths ready units can take and every action the game allows now |
| `Step(actions)` | Gives each listed ready unit its command, then runs frames until the next decision point |
| `Replay(record)` | Resets to the record's seed, takes its steps again and lists every difference in the key state |

**Observations.** A `BattleObservation` holds no game objects. Each unit reports
its id, side, blueprint, faction, hit points, the game's life state, `CanAct` and
`CanMove`, whether it is in combat, position, facing, corpulence, speed, the
command it runs, its standard, move and swift action cooldowns, buffs with their
remaining time, resources and abilities, casts left per spellbook and spell level
(`SpellSlots`), the uses left of the usable items in its quick slots as the action
bar shows them (`ItemUses`: a wand's charges, a stack's count), the abilities
switched on (`TogglesOn`), and whether it is controlled. A controlled unit is ready when it is conscious, the game lets it act
and it has no command left. `Candidates` lists every action the game allows a ready
unit: waiting, attacking each unit the game's player attack checks allow
(`UnitEntityData.CanAttack` and a weapon to attack with), using each entry of its
action bar on each target the entry accepts, switching its toggles, and moving
along each usable path. Two candidates stand for every point on the ground
(`AnyPoint`): `BattleAction.MoveToAnyPoint(unit)` and, for an entry that targets a
point, `BattleAction.UseAbilityAtAnyPoint(unit, ability)`. The decision maker gives
the point with `At(point)` (or `BattleAction.MoveTo(unit, point)` and
`BattleAction.UseAbility(unit, ability, point)`), and the game checks it before
anything changes: a move needs a path the game's agent finds there, an entry needs
the ability to accept the point (`AbilityData.CanTarget`) and, in turn-based combat,
a command that can start there this turn. A refused point rejects the step with the
reason; a candidate without a point is not an action.

**Paths.** The observation lists scenario paths as supplied, and for each ready
unit that can move, the path the game's `UnitMovementAgent.FindPath` finds to every
other unit that is not dead (source `game:UnitMovementAgent.FindPath`). The game's
A* calculates it at once with `AstarPath.BlockUntilCalculated`, as the game's AI
does; paths other units requested earlier in the queue complete then too. Each path
has a stable id, its waypoints and destination, and the distance the source gives
(the game's `Path.GetTotalLength`; the game's interface gives no cost, so `Cost`
stays null). A scenario path is usable only by the unit at its start, while that
unit is ready and still stands within its corpulence (at least 0.5 m) of the path's
first point; otherwise `Problem` says why. The environment never computes, repairs
or measures a path.

**Actions.** `BattleAction.Attack(unit, target)` runs the command a player's click
on the target gives (`ClickUnitHandler.CreateCommand`: the unit's auto-use ability
when it has one, otherwise `UnitAttack.CreateAttackCommand`, which approaches first
when needed) and sets the unit's manual target. `BattleAction.Move(unit, path)` runs the game's player move:
to a path that leads to a unit, `UnitMoveTo` to that unit's position with the
game's approach radius for a click on a unit; to any other path, `UnitMoveTo` to its
destination as a click on the ground does. In real time the game's move commands
find their own path to the destination (only turn-based combat follows a
precomputed one), so the path walked can differ from the path observed;
`BattleStepResult.Walked` reports the distance each unit actually walked.
`BattleAction.UseAbility(unit, ability, target)` uses an entry of the unit's action
bar, and `BattleAction.Toggle(unit, ability)` switches one on or off. The entries
are those the game's action bar offers, collected as its `ActionBarVM` does: the
spells of the unit's spellbooks (`ActionBarSpellbookHelper.Fetch`: prepared and
spontaneous, with metamagic), the usable items in its quick slots (potions,
scrolls, wands), its abilities that are not hidden, cantrips or temporarily
disabled, and its toggles (Power Attack, Rage and the like), each followed by the
conversions its slot offers (spontaneous conversions such as a cleric's cure
spells, an ability's variants). Ids name them by blueprint:
`spell:BOOK:LEVEL:SPELL[+METAMAGIC]`, `item:QUICKSLOT:ITEM`, `ability:ABILITY`,
`toggle:TOGGLE`, with `>CONVERTED` for a conversion. An entry is a candidate while
the action bar lets the player use it (`IsPossibleActive`: resources, spell slots,
and in turn-based combat the turn's actions), on every unit it accepts as a target
(`AbilityData.CanTarget`, as a click with the selected ability checks it; a point
ability targets the unit's position); an ability without target selection acts on
the caster and takes no target. A unit the player controls uses it as the player
does, in real time and in turn-based combat: the slot's click
(`MechanicActionBarSlot.OnClick`) casts an ability without target selection,
switches a toggle or selects the ability, and the pointer on the target and a click
let the game's `ClickWithSelectedAbilityHandler` give the command. A controlled unit
the player does not control (an enemy in self-play) gets the command that click
would give (`UnitUseAbility.CreateCastCommand`, an item's `UnitUseAbility`, or a
custom move or attack), with the game's path in turn-based combat, or its toggle
switched as the slot does; its entries are those whose slot is available, without
the player's turn checks. Usable items go into quick slots with
`BattleSlot.QuickSlot1` to `QuickSlot5`. `MoveTo` is a click on the ground: in
real time the game's move command finds its own path to the point
(`ClickGroundHandler`), in turn-based combat the click goes through the game's
predictions as for a scenario path, and a controlled unit the player does not
control gets the move with the path the game's agent finds. A unit given an action other than waiting or
toggles decides again as soon as it is ready, so a refused click returns at once. A step takes one action per unit besides its toggles: toggles go together with
the unit's command and are switched first, as the player switches them before
clicking; a unit given only toggles waits like `Wait`. `BattleAction.Wait(unit)`
leaves a ready unit idle until the next decision point.
Units not listed keep running their commands, and units of other sides act on the
game's AI. A step with an action that is not among the candidates, a unit that is
not ready or not controlled, or two actions for one unit is rejected with
`BattleActionRejectedException` before anything changes.

**Decision points.** After giving the commands, `Step` runs frames until, after a
frame, a controlled unit that was busy is ready again, the episode is decided, or
`DecisionInterval` (default 6 seconds, one round) has passed. The result has the
new observation, the game time the frames took, why they stopped, the changes of
the battle's key state (`BattleState` lines), the walked distances, the outcome and
the reward.

**Turn-based combat.** In a turn-based scenario an episode starts out of combat,
in real time. Once the game runs combat in turns, only the unit whose turn it is
acts: a controlled unit is ready while the game's `TurnController` waits for its
command (the turn is preparing or acting, and nothing of the unit runs). A step
stops after the frame in which the game waits for a controlled unit's command on a
turn that began during the step, or again on the turn of a unit that acted in it;
`TurnBasedStepLimit` (default 10 minutes of frame time) bounds a step, and reaching
it is an environment failure. A step also fails, with `TurnBasedStallException`, after
a second of frames in which the game's own code can start no turn: in the surprise
round its `CombatController.ChooseNextUnit` skips surprised units and units acting in
the surprise round without an offensive command, and when it skips every unit it
chooses the last one; if that unit is surprised, the game does not count down its
initiative (`UnitCombatCooldownsController`), so its turn never starts. A unit that
started combat with a move and a party member that joined combat a moment later can
lead there. Instead of `Wait`, a ready unit can take
`BattleAction.EndTurn(unit)`. A unit the player controls (a party member) acts
through the player's input. `EndTurn` presses the game's end-turn key (`ForceToEnd`
when `CanEndTurnAndNoActing`). `Attack` moves the pointer onto the target's view,
lets one frame pass so the game's predictions find the path and shorten it to the
movement left (`PathVisualizer`), and clicks; the game's `ClickUnitHandler` turns
the click into the command. `Move` clicks on the ground at the path's destination,
which the game's `ClickGroundHandler` handles. A click the game ignores
(`TurnController.IgnoreClick`) is named in the step's decision. A controlled unit
the player does not control, such as an enemy in self-play, gets the command a click
would give with the path the game's agent finds for it, as the game's AI commands
carry one in turn-based combat (`ForcedPath`); `EndTurn` gives it no command, and the
game ends its turn when it stays idle. The player's unit also has the smart cursor
of the game's `TurnController`. `BattleAction.Attack(unit, target, variant)` takes one
of its variants (`BattleAction.AttackVariants`: a five-foot step or a move, then a
full attack, a single attack or the delivery of a held touch): the attack variants
whose conditions on the unit hold are candidates (a full attack with more than one
attack and a full-round action, a touch with a held charge, a five-foot step while
the unit has it or the target is within reach, a move unless the target is within
reach and the unit has its step), and the plain `Attack(unit, target)` keeps the
variant the game picks. The environment moves the pointer onto the target, lets a
frame pass so the game's predictions enable the variants for the path they find,
switches to the variant with the player's key (`TryChangeSmartAction`), lets the
predictions follow and clicks; when the game does not offer the variant for that
path, nothing is clicked and the decision names the variants it offers. A move on the
ground takes a movement limit the same way (`BattleAction.MoveLimits`: two actions,
one action, a five-foot step, as `TryChangeMovementLimit` cycles them), offered when
the unit has more than one. `BattleAction.Delay(unit, target)` delays the turn until
after another unit of the initiative order, as the initiative tracker does
(`TurnController.DelayInitiaive`), while the game allows it (`CanDelay`: the
player's unit has not acted yet). An attack is a candidate only while the game
would start it this turn: the unit can act and has the action
(`UnitCommandController`'s turn-based check of `CanActInCombat` and
`HasCooldownForCommand`), and the target is within reach or the turn leaves the unit
movement (`TurnController.HasMovement`). Scenario paths are move candidates; the
game's paths to units are not, because a click there is on the unit.

**Outcome and reward.** By default an episode is decided when at most one side
has a conscious unit; `BattleEnvironmentOptions.Outcome` replaces that with the
consumer's own decision from the unit observations. An episode that reaches
`MaxSteps` steps is truncated, which is not a result. `Reward` computes each step's
reward from the observations before and after, the actions and the state changes;
without it the reward is 0. Further steps after the end are refused until `Reset`.

**Failures.** An error during a step, such as an error the game logs and catches or
a frame that cannot run, throws `BattleEnvironmentException` with the episode, step
and actions. It is not an outcome or a training sample: the episode stops until
`Reset`, and the record keeps the error. A cleanup that leaves state behind makes
`Reset` throw `BattleCleanupException`, the process is dirty, and the environment
cannot continue; run the next episodes in a new process.

**Training programs in another process.** `BattleEnvironmentServer.Serve(port,
scenario)` serves the environment to one client on a local TCP port, one JSON object
per line: `start`, `reset`, `observe`, `step` with candidate indices or action
objects (with a point for any-point candidates), `close`. The battle runs in the test
process inside the offline runtime; the client only sends decisions. The battle
fixture's `TrainingServer` test serves while `WOTR_TRAIN_PORT` is set, and
[examples/training](../examples/training/README.md) has Python clients: REINFORCE,
random-rollout planning, on-policy distillation and MaskablePPO through a Gymnasium
interface with action masks. The environment has no save and restore; rollouts
rebuild a state by replaying from the episode's seed.

**Records.** `environment.Record` has the scenario, episode, seed, game version,
library version, initial state, and per step the actions, game time, decision and
the key state afterwards. A seed alone does not reproduce an episode; the actions
do, and `Replay` checks that they do. The game's pathfinding does not make an episode
depend on how fast or busy the machine is (boundary `pathfinding-clock`), for example
when many episodes run at once. `environment.json` counts episodes, steps,
ended episodes and failed episodes under `battles.environment`.

## Runtimes

| `WotrRuntime` (`-p:WotrRuntime=...`) | Process | Use |
|---|---|---|
| `mono` (default) | VSTest's normal .NET Framework testhost and the project's adapter on the game's own Unity Mono runtime (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`, Mono 5.11 in 2.7.0), started by `WotR.Testing.MonoHost` | Matches how the game runs mod code; verified with xUnit v2 |
| `netfx` | .NET Framework 4.8 through `dotnet test` | Any test framework. Differs from the game where noted below |

`WotR.Testing.MonoHost` (`src/WotR.Testing.MonoHost`, a small .NET Framework program
shipped in the NuGet package and built for source consumers) loads the Mono library from the declared
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

The package's VSTest bootstrap registers a runtime-provider extension before
entering the SDK's existing command-line runner, which initializes runtime providers
before processing adapter paths. The extension changes host startup; the existing
adapter discovers and executes tests and VSTest writes the chosen loggers. User
settings are merged per invocation, with supported paths expanded using the SDK's
public settings utility. Mono uses x64, disables AppDomain isolation and disables
xUnit shadow copying. CLR debugger launch on this embedded runtime is unsupported;
other test adapters, collectors and IDE integration have not yet been verified.

Unity Mono binds a simple assembly name to the first copy loaded, so VSTest's
Json.NET would replace the game's older, incompatible Json.NET. For Mono runs,
the integration prepares a local cache of the SDK's .NET Framework host. Only the
SDK Json.NET assembly identity and references to it are renamed to
`WotR.TestHost.Newtonsoft.Json`; corresponding SDK binding redirects are removed.
The SDK method instructions stay unchanged, and the game keeps its original
Json.NET. `environment.json.testHost` records both loaded copies and the original
and isolated host file hashes. The cache is keyed by SDK file contents and is
never part of a package or public artifact. Tools scanning every loaded type can
still see the two libraries' identical type namespaces.

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

## Harmony copies

Every mod binds to whichever `0Harmony` assembly is loaded first, and players' games
have several. WotR 2.7.0 ships Harmony 2.0.4 in `Wrath_Data/Managed` (byte-identical
to the `net45` build of the `Lib.Harmony` 2.0.4 NuGet package), and each
UnityModManager release ships its own: 0.32.4 has 2.3.6 (the `net48` build of
`Lib.Harmony` 2.3.6); its changelog names 2.0.1 for 0.22.1 and 2.0.2 for 0.22.9. The
run cannot know which copy a player's game loads, so it lets you choose the copy it
uses and compares the mods with others.

| Runner | Build property / variable | Values |
|---|---|---|
| `-Harmony` | `WotrHarmony` / `WOTR_HARMONY` | The copy the run loads: `umm` (default), `game`, a `0Harmony.dll` path; on the runner also a `Lib.Harmony` version |
| `-HarmonyReference` | `WotrHarmonyReference` items / `WOTR_HARMONY_REFERENCES` (`\|`-separated) | Copies the mods and their libraries are compared with, in the same forms (default `game`) |

A version such as `2.2.2` is restored as the `Lib.Harmony` NuGet package (NuGet
verifies its signature) and its `0Harmony.dll` used: the first of `net48`, `net472`,
`net45`, `net452` and `net35` it contains, or the one named as in `2.2.2/net35`. Builds
for different target frameworks are different files, so name the framework to
reproduce a specific copy; `environment.json` records the version, path and SHA-256
of every copy (`harmony`).

With another copy the run is the same test run: a mod that calls a member the copy
lacks fails at `mod-load` with `MissingMethodException`, as it would in such a game.
The comparison is static. It finds referenced types, methods and fields (including
through base types) that a copy lacks and warns once per copy and assembly; it does
not find members reached through reflection by name (`AccessTools.Method(type,
"Name")`) or behavior that differs between Harmony versions. Only running on that
copy shows those.

The library's own patches work with Harmony 2.0.4 and later: they do not use
`__args`, and rewritten internal calls end with an unreachable return (see below),
because Harmony before 2.1 cannot inject `__args` and emits invalid IL when patching
a method that ends in `throw`.

## How the game runs without Unity

On either runtime, any method that calls a Unity engine function implemented in
native code has no implementation outside the player. The library therefore builds
a runtime folder under `WotrWorkDirectory` from copies of the inputs. The originals
are never modified. The copies differ in two ways:

1. **Unity internal calls get managed bodies.** By default they throw
   `NotSupportedException("UNITY_NATIVE_UNAVAILABLE: <method>")`, so code that
   needs the engine fails visibly (an unreachable return follows the `throw`, so old
   Harmony versions can patch them). Four narrow rules return instead:
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
| `umm-log` | UnityModManager `Logger.Write`, `WriteBuffers`, `Clear` | Every line goes to `logs/`; `Log.txt` in the installation is never written or deleted |
| `dlc-availability` | `BlueprintDlc.IsAvailable`, `StoreManager` DLC refreshes | Answered from `WotrDlc`; no store is contacted |
| `shader-ids` | `Shader.PropertyToID` | Stable integer per name (visual static constructors) |
| `scene-objects` | `Object.FindObjectsOfType`, `Resources.FindObjectsOfTypeAll` | Empty: no scene is loaded |
| `bug-report-service` | `ReportingUtils` constructor | Skipped; it would contact the developer's report server |
| `graphics-settings` | `GraphicsSettingsController` constructor, graphics quality autodetection | Skipped |
| `audio-service`, `character-atlas-service` | `SoundState`, `CharacterAtlasService` | Registered without running constructors that create GameObjects |
| `referenced-assets` | `BlueprintReferencedAssets.Get` during blueprint loading | Real entries; ScriptableObjects mapped from `blueprint.assets`, native assets (sprites, textures, prefabs, meshes) null. Mapped ScriptableObjects read as alive under Unity's checks (`if (asset)`, `asset != null`), as loaded assets do; fields of Unity's own structs that Unity serializes under another name (`Bounds.m_Extent`, `LayerMask.m_Bits`) go to their managed fields |
| `settings-asset` | `SettingsValues` | Mapped from the real asset, including difficulty presets |
| `runtime-images` | `Texture2D` constructors, `ImageConversion.LoadImage`, `Sprite.Create` | Images a mod builds at run time (icons from PNG files) have no native texture: constructors are skipped, `LoadImage` returns false and `Sprite.Create` returns null, like native assets |
| `unity-random` | `UnityEngine.Random` native entries (`InitState`, integer and float `Range`, `value`, `state`, legacy `seed`) | One `System.Random`, seed `1` at startup. Integer results are `[min, max)`. Float `Range` and `value` are `[min, max)`. This is not the Unity player's sequence. `Random.state` values work only in the same process. Negative seeds follow `System.Random`'s absolute value |
| `system-random-observe` | `System.Random` constructors | Counted only. The game's own instances stay in place and do not take the game seed. An unseeded construction during `RuleScope` is reported on that result |
| `profiling-counters` | `Owlcat.Runtime.Core.ProfilingCounters.Counter.AddMeasurement` | Skipped. It files a timing under the current Unity frame (`Time.frameCount`), which does not exist offline; it wraps, for example, the event raised when a unit's position changes. Only profiler statistics are affected |
| `unity-math` | `Quaternion`, `Matrix4x4`, `Vector3` and `Bounds` native math | Managed versions with Unity's conventions (left-handed basis, Euler order Z, X, Y): look rotation, Euler conversion, inverse, angle-axis, from-to, lerp and slerp, matrix inverse, determinant, transpose, identity check and TRS, vector rotate-towards and slerp, and whether bounds contain a point (a point on a face is inside). Used for example when a unit turns or the game's A* code builds a graph transform. Results can differ from Unity's native code in the last bits of a float |
| `navigation-graph` | `AstarPath.active` and the area's navigation graph | Read as `NavMeshManager` does, from the `.nav` bundle `Bundles/locationlist.json` names, and deserialized by the game's A* code into an `AstarPath` component without a GameObject, with the serialized settings of the `AstarPath` in the area's mechanics scene (its `.scenes` bundle, read on demand, or a snapshot's `navigation-settings.json`; class defaults and a warning without either); path requests run on A*'s non-threaded path processor, updated once per battle frame |
| `physics-scene` | `PhysicsScene` native queries, the `Physics.defaultPhysicsScene` getter and the entity-bounds scene while a battle runs its game loop | Only during a battle with an area; outside battles they fail as `unity-runtime-required`, as before. The default physics scene is a `PhysicsScene` with no handle. No level colliders exist offline: casts hit nothing and overlaps find nothing. The game's `EntityBoundsController` creates its entity-bounds scene (a scene with no level, valid only during the battle) and keeps each unit's bound sphere at the unit's position with its corpulence as radius; on the unit bounds layer those spheres answer, as the game's entity-bounds physics scene does. Vision range uses them. Heights follow the navigation mesh, not ground colliders |
| `unit-view-stand-in` | `UnitEntityView` of battle units and the components on its GameObject; the view side of `AttachView`/`DetachView`; `Polymorph.TryReplaceView`/`RestoreView` for stand-ins | A view with no GameObject, model or animator that Unity's checks read as live, with the serialized fields of the unit's real view prefab, read on demand from its `.unit` bundle (`CorpulenceOverrideBySize` replaces only the corpulence; without the bundle, an override or `CorpulenceFallbackBySize` gives the corpulence and the rest keeps class defaults), carrying the game's own `UnitMovementAgent`, seeker and path modifiers. The game's `AttachView` and `DetachView` attach it; `EntityViewBase.AttachToData` and `DetachFromData` set up and release only what the mechanics read. Their transform reads as the unit (its position and orientation); writes to its position or rotation are dropped, because only a model would show them. The bound sphere's transform is moved by the game's `EntityBoundsController`. Enabled flags come from the prefab; enabling or disabling the movement agent runs its own `OnEnable` or `OnDisable`. The stand-in GameObjects keep an active flag: deactivating one runs `OnDisable` of its enabled agent, activating it `OnEnable`, as Unity does, and physics overlaps skip disabled bound spheres. Unity's component lookups on a stand-in GameObject (`GetComponent`, `TryGetComponent`, `GetComponents`, its `transform`) answer from the stand-in's components; presentation components the game's interface code expects (a line renderer, decals) are stand-ins of the same kind, with their enabled flags. The stand-ins are distinct objects under Unity's `==`; `GetInstanceID()` needs native code and fails as `unity-runtime-required`. A polymorph swaps stand-ins (its prefab with size scaling off, then the unit's own); the view replacement record, the command interruption and waking the unit run as in the game, models and the visual fade are skipped. Removed with `DetachView` before the unit is destroyed |
| `unit-weapon-visuals` | `UnitEntityView.HandleAttackCommandRun`, `HideOffWeapon`, `UnitHandEquipmentController.DidUpdate` for stand-ins | Skipped: weapon models in the hands, occlusion clipping, and waiting for the hand models to match the combat state |
| `unit-hit-effects` | `HitPlayer.PlayDamageHit` when a stand-in is involved | Skipped: blood, hit effects and sounds. The game takes the same early return for units without a live view; `RuleDealDamage` deals the damage as shipped |
| `debug-draw` | `Debug.DrawLine` | Skipped: editor scene-view lines, which a player build does not draw either |
| `unit-death-visuals` | `UnitEntityView.HandleDeath` for battle units | Skipped: the blood and dismemberment effect, the fade, occlusion clipping, a swarm's health display, the prefab's death effects and moving the click collider with the torso. The death itself is `UnitLifeController`'s and runs as shipped |
| `fx` | `FxHelper` spawn methods and `AbilitySpawnFx.Spawn` while a battle runs | Skipped: visual effects. Ability effects are skipped as when their execution context disables effects; `FxHelper` returns no handle, which its callers allow for |
| `sound` | `SoundEventsManager.PostEvent` and the action bar's click sound (`MechanicActionBarSlot.PlaySound`) while a battle runs | Skipped: sound events; the game gets no playing id (0). An action bar slot the battle environment clicks plays no click sound |
| `ability-cast-fx` | `UnitUseAbility.CastAbilityFx` for battle units | Skipped: the ability's spawn effects, the precast effects and the caster's spell voice; the ability is cast and resolved by the game's code |
| `unit-highlight` | `UnitEntityView.UpdateHighlight` for battle units | Skipped: the highlight of a unit under the mouse, in a drag box or among the objects the player can use |
| `camera` | `Game.CameraController`, `TurnController.TryScrollToUnit` and `IslesController.TickCameraRotation` while a battle runs | No camera exists in a battle. `Game.CameraController` is the default mode's own `CameraController` without its `Activate` or `Tick`, which bind the follow key and move the camera rig: code that follows or releases a unit with the camera sets the follower's state, and nothing scrolls. When a turn starts the camera does not scroll to the unit, so the turn is prepared at once, as when the player switches the scroll off. Reading the camera rig's rotation for the Isles is skipped; the isles themselves tick |
| `external-control` | `AiBrainController.TickBrain` for units of `BattleScenario.ControlledSides` | Skipped for those units only, at the consumer's request: the game's AI does not choose their actions in real time or on their turns, not even the actions it takes for party members with their AI switched off. Units of other sides keep the game's AI. A difference from the game, counted in the report |
| `asset-names` | `Object.name` of ScriptableObjects mapped from `blueprint.assets` | The asset's serialized `m_Name`, which Unity keeps in native memory; the game reads it, for example, as the key of a keyboard binding. Any other object's name fails as `unity-runtime-required` |
| `loading-process` | `LoadingProcess.Instance` while a battle runs | A `LoadingProcess` without a GameObject for the battle: a battle loads no scenes and shows no loading screens. The game's code asks it as usual (the turn-based `CombatController` resets the manual loading screen when combat starts); its `Update` and coroutines never run |
| `main-thread-dispatcher` | UniRx `MainThreadDispatcher` while a battle runs | A dispatcher without a GameObject for the battle. Actions the game posts to the main thread (for example when turn-based combat ends) run in the dispatcher's own `Update` at the end of every battle frame and once when the battle stops, as Unity's next frame would; its coroutines are not started |
| `pointer` | `Game.ClickEventsController` and the pointer's object, position and clicks while a battle runs | The game's `DefaultPointerController`, set as `SetupPublicControllers` sets it, without its `Tick`: no mouse, camera or level to cast rays into. The battle environment points it at a unit's view or a point of the ground (no object, as no level is loaded) and clicks as `PointerController.Tick` handles a left click: the game picks the handler by its priorities, the current turn may ignore the click, and the handler's `OnClick` runs. Hover changes are announced as the pointer announces them. At cleanup, before the units go, the pointer moves off the unit it is on and the player's selection is cleared as an unloading area clears it (`SelectionCharacterController.OnAreaBeginUnloading`); the reset compares both |
| `path-preview` | `PathVisualizer` while a battle runs | The turn-based path preview without a GameObject; its line renderer and decals are stand-ins without native objects. Its `Update` runs every battle frame, the game's predictions ask it for the path of the command under the pointer, and its movement simulation shortens a move to the movement left. Only drawing the line (`DecimateLine`, `SetGradientsToRenderer`: points, colors, materials, decal positions) is skipped |
| `selection-ui` | `SelectionManagerBase.Instance` (`Game.UI.SelectionManager`) while a battle runs | A `SelectionManagerPC` without a GameObject, registered as its `Awake` registers it, for the click handlers that ask which selected unit acts. The selection is the game's `Game.SelectionCharacter` state, which the `TurnController` sets to the unit whose turn it is. Selection marks are never created, and party changes, which it handles with UI coroutines, are not followed |
| `cursor` | `CursorController.SetCursor` while a battle runs | Skipped: setting the mouse cursor's image. The game's `CursorController` still decides which cursor it would show |
| `input` | `UnityEngine.Input` keys, buttons, axes and mouse position while a battle runs | No keyboard or mouse: no key or button is held, pressed or released, axes are at rest and the mouse is at the origin, so the game takes its branches for no modifier key held (no Ctrl to force a move) |
| `move-target-marker` | `UnitEntityView.OnMovementStarted` for battle units | Skipped: when a directly controllable unit starts a path, it only places the UI's destination marker (`ClickPointerManager`), which does not exist offline. The movement runs as shipped |
| `game-mode` | The game's default mode during a battle with an area: `Game.DoStartMode`, `Game.Tick` and `Game.DoStopMode` | The default mode `GameModesFactory` builds, without the presentation controllers listed under [Battles](#battles). It is pushed on the game's mode stack, started and activated, and stopped and popped at cleanup, as `DoStartMode` and `DoStopMode` do; of the public controllers only those the mechanics use (hand equipment, combat engagement) are set, not the pointer, interaction highlight or camera. Each frame runs `Game.Tick`'s scheduled actions, real time, mode tick and temporary entities, not its sound, atlas and statistics updates. A request to switch to another mode fails the frame |
| `projectile-view` | `ProjectileController.CreateView`, `ProjectileController.Clear` and `GameObjectsPool.Release` of projectile views, and `TrajectoryCalculator.CalculateShift`, while a battle runs | A projectile (an arrow, a ray, a cone, a fireball) gets a stand-in view object that keeps the position and rotation its flight gives it, instead of its prefab from the effects pool; the pool releases it, and the controller's `Clear` (when the mode stops) releases the views it clears, as destroying their parent object does in the game. The launch position follows `CreateView`; a source bone is a point of the unit's model, which battles do not load, so the game's own fallback for a missing bone applies: the launcher's point one meter up, then the corpulence offset. Target offset, launch, flight, hit and miss are the game's. Cast effects, trails, the source bone's scale and the trajectory's visual shift of the view (an `AnimationCurve`) are skipped; the flight that decides the hit does not use the shift. Projectiles launched in the controller's last tick are dropped with the battle, since the game's single controller would pass them to the next one |
| `pathfinding-clock` | `AstarPath.maxFrameTime` and the time slice of A*'s path return queue while a battle runs | The game's A* code limits its work per frame by the computer's clock: the scene's `maxFrameTime` in milliseconds of path searching, after which a search or the rest of the queue continues in the next frame, and 0.5 ms of handing finished paths back, so the frame a path finishes in would depend on the machine's speed and load. A battle frame does all of it, as a machine fast enough for both limits does: `maxFrameTime` is 10^12 ms (`battles.navigation` keeps the scene's value), and the paths the time slice leaves go back right after A*'s `Update`, in the same frame and order. Paths requested during that hand-back are searched in the next frame, as in the game. Path results are the game's; which frame they finish in follows the game's order only |
| `frame-clock` | Unity's `Time` (`frameCount`, `realtimeSinceStartup`, `time`, `unscaledTime`, `deltaTime`, `unscaledDeltaTime`, `timeScale`) while a battle runs its game loop | Each battle frame is 1/30 s of real time. It starts as a Unity frame does: the frame count, real time and Unity's scaled and unscaled time advance before A* and the controllers run, and `Time.deltaTime` is the frame scaled by `Time.timeScale`. The game's own `TimeController` runs in the default mode and sets `Time.timeScale`, the frame durations and game time, including its turn-based time scales. Between battle frames each frame-count read is a new frame, so per-frame caches are never stale; outside battles these calls fail as `unity-runtime-required`, as before |

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
- **Character-creation pregens.** Pregens such as `MC_Human_M_Cavalier_Base` are
  templates for character creation. Their class levels come from a feature list
  with `AddClassLevels`, but the blueprint's `ClassLevelLimit` is 0, so the game
  records the class levels as a level-up plan and the unit has character level 0.
  Mythic classes are capped only by `MythicLevelLimit`, which the cavalier pregen
  lacks, so it does get its 2 mythic starting and 10 Angel mythic levels. Being in
  the player faction, its starting weapons and armor fail the game's proficiency
  check in `CanBeEquippedBy` ("can't insert item ... to slot") and stay in the
  inventory: it is unarmed and unarmored. This is the game's behavior for such a
  blueprint outside character creation. For battles use units that the blueprint
  levels itself, for example NPC blueprints or player-faction test units with
  `AddClassLevels` and no level limit.
- **Battle loop.** Battles run the game controllers listed under
  [Battles](#battles), not the whole default mode. In particular:
  - Lasting area effects (Grease, Web, Entangle and the like) need the game's
    area effect view, a GameObject the game creates with native code; casting one
    fails the step as `unity-runtime-required`. Effects that act at once, including
    those a projectile delivers (rays, cones, Fireball), run.
  - No camera: no unit is in the camera's view, so a dead unit's body is not
    revealed and its view deactivates, as for a body off screen in the game.
  - No fog of war: every unit counts as visible to the player.
  - No animations: a command acts on its first tick, as the game's code does when
    a command has no animation, so act timing and the spacing of iterative
    attacks differ from the game.
  - Units with doll data from character creation, and polymorphs into a doll,
    need a corpulence override or fallback for their size: the game makes their
    view from the doll room character, which battles do not read.
- **Scene geometry.** The navigation mesh is the area's real one, so paths go
  around walls and obstacles. Level colliders and sight blockers are not loaded:
  units stand at navigation-mesh height, nothing blocks line of sight
  (`LineOfSightGeometry` with no blockers, as before the game builds it), and only
  battle units are in the physics scene.
- **Turn-based combat.** `BattleScenario.TurnBased` runs the game's turn-based
  combat; without it battles run real-time combat and rules that branch on
  turn-based state take their real-time path. In the battle environment, turn-based
  moves are clicks on the ground at scenario path destinations and at points the
  decision maker gives. Smart cursor variants are offered by the conditions on the
  unit; whether the game offers one for a target depends on the path its predictions
  find, which the click checks. In turn-based
  self-play, a controlled enemy's commands carry the path the game's agent finds,
  not the path the game's AI scores and trims. The surprise-round dead end of the
  game's code (see [Battle environment](#battle-environment)) ends the episode with an
  environment failure; it follows from the decompiled code and was not reproduced in
  the running game.
- **Area parts.** A battle loads its area as the current area part. The game picks
  the part from the main character's position when it loads an area without an
  enter point, and loads that part's navigation graph; battles use the area's own
  graph and bounds.
- **Creature types.** The game gives undead and constructs no Constitution; pick
  test units whose rules match what is asserted.
- **Harmony.** Runs use one `0Harmony` copy (UnityModManager's unless `-Harmony`
  says otherwise); which copy a player's game loads is not known. The comparison
  with other copies checks signatures only.
- **Loaders.** Only UnityModManager mods are supported, alone or with declared
  dependency mods. OwlcatModification mods are not.
- **DLC ownership.** `WotrDlc` decides which DLCs exist; nothing asks a store.
  `local` reads Steam's installed depots only.
- **Player state.** No save is loaded. The player object exists with no main
  character, an empty party and game time zero; code that needs more (for
  example `PrerequisitePlayerHasFeature`) logs errors when units are created.
  Battles can set a main character (see [Battles](#battles)).
- **CI.** Public CI has no game files, so it cannot build or run these tests.
