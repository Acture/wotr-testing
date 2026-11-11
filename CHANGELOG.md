# Changelog

## Unreleased

- Offline `UnityEngine.Random` now uses one `System.Random` (startup seed 1) with
  `GameRandom.Init`, `Save`, `Restore` and `Record`. Integer results are
  `[min, max)`; float `Range` and `value` are `[min, max)`. This is not the Unity
  player's sequence. `Random.state` values are valid only in the same process.
- `ActionSampleRandom` is a separate stream and does not read or write the game
  RNG. `game.Fuzz` saves and restores the game RNG around each execution.
  `FuzzOptions.BeforeExecution` and `AfterExecution` run inside that pair.
  Argument generation still uses the fuzzer's own stream.
- `system-random-observe` counts `System.Random` constructors without replacing
  the game's own instances. `RuleScope` fails an operation when that operation
  logs an error the game caught. `environment.json` records `gameRandom`.
- ScriptableObjects mapped from `blueprint.assets` now read as alive under Unity's
  checks (`if (asset)`, `asset != null`), as loaded assets do; before, the game's code
  treated them as missing (for example an area's bounds). `Bounds` and `LayerMask`
  values in assets now keep their extents and masks (Unity serializes them as
  `m_Extent` and `m_Bits`). Their `Object.name` is the asset's name (boundary
  `asset-names`).
- Battle fixtures: `BattleScenario` describes units on each side by blueprint, with
  faction, class levels, base stats, equipment, facts, abilities, buffs, resources,
  positions, an optional main character and paths that already exist.
  `OfflineBattle.Start(scenario, seed)` applies each entry through the game's API
  and checks it; equipment the game refuses and errors logged while building fail
  the build. `Reset()` rebuilds the same initial state in the same process and
  compares game-wide state (EventBus and Rulebook subscriptions, entities, party
  inventory, main character, game time, game RNG and more) with the state before
  the build. A cleanup that leaves anything, or logs an error the game caught,
  marks the process dirty: it refuses further battles and the run fails. A battle
  never disposed fails the run as well. `environment.json` records `battles`.
- Battle game loop: with `BattleScenario.Area` set, a battle loads the area and its
  real navigation graph (read from the game's `.nav` bundle as `NavMeshManager`
  does) and runs part of the game's default mode on its units:
  `battle.RunUntilIdle(limit)` and `battle.Run(duration)` run the game's default
  mode as `Game.StartMode` starts it, with every controller except presentation
  (input, pause, camera, fog of war, effects, animation, UI windows, game over):
  the game's AI, initiative, abilities, projectiles, area effects, buffs and life
  and death among them. `BattleScenario.ControlledSides` names sides the game's AI
  never decides for, so their units act only on the commands given to them. Units walk A* paths with the
  game's own `UnitMovementAgent`, see and remember each other, join combat, and
  provoke attacks of opportunity when they leave an enemy's reach. Frames follow
  the game's time scales and fail when those pause the game. Units have a
  view stand-in without a model. Fog of war, animations, level colliders and sight
  blockers are not supported (see the known limits). A* takes the settings of the area's own `AstarPath`
  from its mechanics scene bundle, read on demand. Snapshots do not copy the scene
  bundles: `New-WotrSnapshot.ps1` writes the `AstarPath` settings of every mechanics
  scene to `navigation-settings.json` (with `src/WotR.Testing.Snapshot`, which needs
  the .NET SDK), and battles read them from there. Without either, A* keeps its class
  defaults and the report warns.
- `BattleEnvironment` runs a battle as a decision loop for an external decision
  maker such as a training program: `Reset(seed)`, `Observe()` (units, the game's
  paths to other units and every action the game allows), `Step(actions)` (the
  game's attack and move commands for units of controlled sides, then frames until
  the next decision point while the game's AI fights for the other sides),
  self-play by controlling every side, turn-based combat through the game's own
  turns and the player's input (`BattleScenario.TurnBased`, `BattleAction.EndTurn`),
  every entry of a unit's action bar (`BattleAction.UseAbility` for spells with their
  spontaneous conversions and metamagic, abilities and their variants, and usable
  items in quick slots; `BattleAction.Toggle` for toggles), moves to and abilities at
  points on the ground the decision maker gives (`MoveToAnyPoint`, `UseAbilityAtAnyPoint`,
  checked by the game), the turn-based smart cursor's variants and movement limits
  (`BattleAction.Attack(unit, target, variant)`, `Move(unit, path, limit)`) and
  delaying a turn (`BattleAction.Delay`), spell slots, item uses and toggles in the
  observation,
  configurable outcome and reward, a step
  limit, records per episode and `Replay(record)`. Errors during a step are
  environment failures with the episode and step, never outcomes; a turn-based
  surprise round in which the game's own code starts no turn fails the step at once
  with `TurnBasedStallException`.
- `BattleSlot.QuickSlot1` to `QuickSlot5` put usable items in a battle unit's quick
  slots. `BattleScenario.FrameLogAllowances` allows errors the game logs during
  battle frames that the scenario explains; the game's own known logs (a touch
  spell's duplicate EventBus subscription) are allowed with their reason and counted
  in `battles.allowedFrameLogs`.
- Battle units take their view's serialized fields from their real view prefab,
  read on demand from the game's `.unit` bundle as `Instantiate` does: corpulence
  (used by reach, engagement, attacks of opportunity, spacing and vision), the size
  override and the movement agent's acceleration, speeds and avoidance. New
  `BattleScenario.CorpulenceOverrideBySize` replaces only the corpulence of units
  of a size; `CorpulenceFallbackBySize` gives a corpulence used only when the
  inputs lack a unit's bundle. A unit whose bundle is missing and whose size has
  neither fails the build as environment-missing. The game's `AttachView` attaches
  the view, so fact, part and view-attached hooks run. A polymorph swaps the view
  for one of its own prefab and back, as the game's `Polymorph` does. The game's
  `EntityBoundsController` keeps the units' bound spheres, and its
  `GarbageEntityController` disposes items left without an owner. Units with doll
  data from character creation need a corpulence override or fallback.
- Snapshots now include `Bundles/locationlist.json` and the `.nav` bundles (about
  95 MB) for battle navigation; make a new snapshot to use battles with an area.
  Unit view prefabs (`.unit`, about 7.5 GB) are not copied; with a snapshot, set a
  corpulence override or fallback for each size a battle uses.
- New boundaries `navigation-graph`, `physics-scene`, `unit-view-stand-in`,
  `unit-weapon-visuals`, `unit-hit-effects`, `unit-death-visuals`, `unit-highlight`,
  `ability-cast-fx`, `fx`, `sound`, `camera`, `debug-draw`, `move-target-marker`,
  `game-mode`, `external-control`, `frame-clock`, `pathfinding-clock`, `projectile-view`, `asset-names`,
  `loading-process`, `main-thread-dispatcher`, `pointer`, `path-preview`,
  `selection-ui`, `cursor` and `input` for battles. With `pathfinding-clock`, a battle
  frame does all of A*'s path work instead of stopping at its limits on the
  computer's clock, so episodes do not depend on the machine's speed or load.
- New boundary `unity-math`: managed versions of Unity's native quaternion, matrix
  and vector math. Code that reached these functions failed as
  `unity-runtime-required` before and now runs, also outside battles; the results
  are the same computation, not an assumed scene. `Debug.DrawLine` is skipped
  everywhere, as a player build draws nothing. Physics queries, `Time.frameCount`
  and `Time.realtimeSinceStartup` are answered only while a battle runs its game
  loop; outside battles they still fail as `unity-runtime-required`.
- New boundary `profiling-counters`: Owlcat profiling counters no longer reach
  Unity's native `Time.frameCount`, so setting a unit's `Position` works offline.
- Documented why character-creation pregens such as `MC_Human_M_Cavalier_Base`
  have character level 0, Angel mythic levels and no starting gear equipped
  offline.

## 0.1.0-preview.3 - 2026-10-18

- New `WotR.Testing.Fuzzing` component: mod authors register an interface and an
  instance factory with `Fuzz.Check<IMyApi>(...)`; the library automatically
  generates method calls, argument values and stateful sequences. Failures are
  shrunk and saved for exact replay. Supports FsCheck generators and explicit
  codecs for custom argument types, inherited interfaces, overloads and Task
  methods; fresh targets are disposed after each sequence or shrink attempt.
- `WotrGameFixture.Fuzz<IMyApi>(...)` records results in `environment.json` and
  classifies unavailable Unity paths separately. All-unavailable runs fail.
  Fuzzing detects unexpected exceptions; gameplay invariants need assertions in
  the selected API or separate property tests. See [interface fuzzing](docs/fuzzing.md).
- `WotR.Testing` also includes FsCheck.Xunit 3.5.0 for author-written `[Property]`
  tests with generated inputs, custom generators, shrinking and seed replay.
- Mono installs the offline assembly resolver before SDK test discovery. This
  prevents an early game-typed field in a test adapter from caching an unresolved
  game assembly reference before the test assembly's module initializer runs.
  SDK and game method instructions remain unchanged; reports record the managed
  bootstrap's path and hash.

## 0.1.0-preview.2 - 2026-10-18

- `dotnet test` is the default entry point, including for NuGet-only consumers.
  The package automatically starts VSTest's ordinary testhost on the game's Unity
  Mono runtime and uses the project's existing adapter; discovery, Theory rows,
  filters, user runsettings, `--no-build` and standard loggers such as TRX work
  through VSTest. PowerShell is no longer required to run tests. Requires .NET SDK
  8 or later on Windows; verified with SDK 10.0.401 and xUnit v2.
- The local Mono VSTest host isolates its Json.NET assembly identity so it cannot
  replace the game's incompatible Json.NET. SDK method instructions and game
  method bodies are unchanged; both copies and host-file hashes are recorded in
  `environment.json.testHost`. No SDK cache or game files are packaged.
- `WotrRuntime=netfx` explicitly selects .NET Framework. `WotrTestTimeoutSeconds`
  (default 900) and `WotrMinimumPassed` (default 1) configure the run. Fresh reports
  are checked for the selected runtime, initialization errors, unexplained skips,
  zero passing tests and missing files. The existing PowerShell runner is retained
  for compatibility. Migration: replace script-path lookup and invocation with
  `dotnet test <project>`; replace `-Runtime netfx` with `-p:WotrRuntime=netfx`.

## 0.1.0-preview.1 - 2026-10-16

- Harmony copies: `-Harmony` (`WotrHarmony`, `WOTR_HARMONY`) chooses the
  `0Harmony.dll` a run loads: `umm` (default, as before), `game` (the game's own
  2.0.4), a file, or on the runner a `Lib.Harmony` NuGet version (`2.2.2`,
  `2.2.2/net35`). Mods and their libraries are compared with `-HarmonyReference`
  copies (default `game`); members a copy lacks are warned and listed in
  `environment.json` (`harmony`). A mod calling `CodeInstruction.LoadArgument`
  passed with UnityModManager's 2.3.6 but fails at `mod-load` with `-Harmony game`,
  as it does in such a game.
- The library's own patches work on Harmony 2.0.4: the Unity log boundary no longer
  uses `__args`, and rewritten internal calls end with an unreachable return. Runtime
  folders are rebuilt once.

- NuGet packages (first preview `0.1.0-preview.1`): `WotR.Testing`, a meta-package
  that references the components, and `WotR.Testing.Offline` with the library, its
  build targets (imported automatically, also through `WotR.Testing`), and in
  `tools/` the runner, the snapshot script and `wotr-mono-host.exe`;
  `WotrToolsDirectory` locates them. Built on a maintainer machine with
  `dotnet pack`; a package holding any file outside its allowlist is deleted. Git
  submodules keep working.

- `WotR.Testing.Offline` extracted from the AttributeFeats offline tests: real WotR
  2.7.0 assemblies, blueprint pack, settings and enGB localization; mods loaded
  through their `Info.json` entry method; classified results and `environment.json`
  reports. Available as preview NuGet packages or a Git submodule.
- Tests run on the game's own Unity Mono runtime by default (`-Runtime mono`,
  through `WotR.Testing.MonoHost` and the xUnit v2 console runner); `-Runtime netfx`
  keeps the .NET Framework mode. **Breaking:** projects that used another test
  framework must pass `-Runtime netfx`.
- Dependency mods (`WotrDependencyMod`), loaded in `Requirements`/`LoadAfter` order
  with version checks. `ModEntry` now has `Assembly`, started/active state and a
  per-run copy of the mod folder as `Path`, and is registered with UnityModManager.
- New `runtime-images` boundary: images a mod builds at run time are null.
- On .NET Framework, mod copies have `beforefieldinit` cleared so type initializers
  run in Mono's order; method bodies are unchanged. Projects that asserted the mod
  copy is byte-identical should compare method bodies with `IlComparison` instead.
- `Application` path boundaries return `/`-separated paths, as Unity does.
- Runner: `-MinimumPassed` (default 1; skipped tests do not count), arguments passed
  without quoting problems, old runtime and per-run folders cleaned up.
- `New-WotrSnapshot.ps1` refuses a destination that overlaps the installation and
  includes `MonoBleedingEdge` for the Mono runtime.
- DLC availability is a declared boundary (`dlc-availability`) chosen with `WotrDlc`
  (`all` by default, `none`, `local` or a list); the report shows which DLCs were
  asked about and which mod blueprints are DLC-gated. No store is contacted.
- UnityModManager log lines are captured as written (`umm-log`), written per source
  to `logs/` (gzip above 5 MB) and summarized in `environment.json`; the
  installation's `Log.txt` is never touched.
- Mods shipping different copies of one library no longer fail the run: the first
  copy in load order is used, with a warning that the game may use another copy. The runner
  warns when BlueprintCore is older than the latest release. Warnings never fail a
  run.
- Verification records moved from root `STATUS.md` to the optional private
  `notes/` submodule. Public builds and repository checks do not require it.
- NuGet release workflow: locally built packages are checked again and published
  through GitHub OIDC trusted publishing, without uploading game files or using
  a long-lived API key.
