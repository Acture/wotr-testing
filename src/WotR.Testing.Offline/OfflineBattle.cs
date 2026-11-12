using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Classes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Items;
using Kingmaker.Items.Slots;
using Kingmaker.PubSubSystem;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Buffs.Blueprints;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// One battle built from a <see cref="BattleScenario"/>: real units from blueprints, changed only through the game's
    /// APIs, with the state the battle owns tracked so <see cref="Reset"/> can rebuild the same initial state.
    /// </summary>
    /// <remarks>
    /// Reset runs in the current test process. Cleanup destroys and disposes the units as the game's
    /// <c>EntityDestructionController</c> does, removes what the battle added, restores game time and the game RNG, and
    /// then compares the game-wide state with the state before the battle was built (<see cref="BattleCleanup"/>). If
    /// anything is left, the process is marked dirty and refuses further battles; it is never reused on trust.
    /// Only one battle can be active in a process, because the game state it changes is global.
    /// </remarks>
    public sealed class OfflineBattle : IDisposable
    {
        private const string UniqueIdPrefix = "wotr-battle:";

        /// <summary>True for a unit a battle built, also while the battle is still building it.</summary>
        internal static bool IsBattleUnit(Kingmaker.EntitySystem.Entities.UnitEntityData unit)
            => unit?.UniqueId != null && unit.UniqueId.StartsWith(UniqueIdPrefix, StringComparison.Ordinal);

        private readonly List<BattleUnit> units = new();
        private readonly List<object> subscriptions = new();
        private readonly List<BlueprintGuid> temporaryBlueprints = new();
        private BattleBaseline baseline;
        private Kingmaker.EntitySystem.SceneEntitiesState battleState;
        private Kingmaker.EntitySystem.AreaPersistentState area;
        private Kingmaker.EntitySystem.AreaPersistentState previousArea;
        private LoadedArea previousLoadedArea;
        private bool areaLoaded;
        private BattleNavigation navigation;
        private BattleLoop loop;
        private GameRandomState outerRandom;
        private bool mainCharacterSet;
        private readonly List<BattleUnit> companions = new List<BattleUnit>();
        private bool? previousTurnBased;
        private BattleDifficulty previousDifficulty;
        private bool disposed;

        private OfflineBattle(BattleScenario scenario, int seed)
        {
            Scenario = scenario;
            Seed = seed;
        }

        public BattleScenario Scenario { get; }

        /// <summary>Seed given to <see cref="GameRandom.Init"/> before the units are built.</summary>
        public int Seed { get; private set; }

        /// <summary>Number of builds of this battle: 1 after <see cref="Start"/>, increased by every <see cref="Reset"/>.</summary>
        public int Generation { get; private set; }

        public IReadOnlyList<BattleUnit> Units => units;

        /// <summary>Paths from the scenario, with their endpoints mapped to this build's units.</summary>
        public IReadOnlyList<BattlePath> Paths { get; private set; } = Array.Empty<BattlePath>();

        /// <summary>Key state right after the last build; equal for every build of one scenario and seed.</summary>
        public BattleState InitialState { get; private set; }

        /// <summary>Result of the last cleanup (by <see cref="Reset"/>, <see cref="Dispose"/> or a failed build).</summary>
        public BattleCleanup LastCleanup { get; private set; }

        /// <summary>Builds the scenario. A failed build cleans up what it created before the exception is thrown.</summary>
        public static OfflineBattle Start(BattleScenario scenario, int seed = GameRandom.DefaultSeed)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            scenario.Validate();
            var battle = new OfflineBattle(scenario, seed);
            BattleWorker.Enter(battle.Describe());
            try
            {
                BattleBaseline.PreparePlayerContext();
                battle.Build();
                return battle;
            }
            catch
            {
                battle.disposed = true;
                BattleWorker.Exit();
                throw;
            }
        }

        public UnitEntityData Unit(string id)
            => units.FirstOrDefault(u => u.Id == id)?.Unit ?? throw new KeyNotFoundException($"Battle {Scenario.Name} has no unit {id}.");

        public IEnumerable<UnitEntityData> Side(string side) => units.Where(u => u.Side == side).Select(u => u.Unit);

        public BattlePath Path(string id)
            => Paths.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException($"Battle {Scenario.Name} has no path {id}.");

        /// <summary>Current key state of the battle units, game time and game RNG.</summary>
        public BattleState Observe()
        {
            ThrowIfDisposed();
            return BattleState.Capture(this);
        }

        /// <summary>Subscribes a handler to the game's EventBus for this build; cleanup unsubscribes it.</summary>
        public void Subscribe(object subscriber)
        {
            ThrowIfDisposed();
            if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
            EventBus.Subscribe(subscriber);
            subscriptions.Add(subscriber);
        }

        /// <summary>
        /// Adds a blueprint that exists only for this build, such as a test ability. A guid already in the cache is
        /// refused, so the battle never replaces a game or mod blueprint. Cleanup removes it again.
        /// </summary>
        public T AddBlueprint<T>(T blueprint) where T : SimpleBlueprint
        {
            ThrowIfDisposed();
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            if (ResourcesLibrary.TryGetBlueprint(blueprint.AssetGuid) != null)
                throw new InvalidOperationException($"Blueprint {blueprint.AssetGuid} already exists; a battle only adds new blueprints.");
            ResourcesLibrary.BlueprintsCache.AddCachedBlueprint(blueprint.AssetGuid, blueprint);
            temporaryBlueprints.Add(blueprint.AssetGuid);
            return blueprint;
        }

        /// <summary>Advances game time with the game's <c>Game.AdvanceGameTime</c>; cleanup restores the time before the build.</summary>
        public void AdvanceGameTime(TimeSpan delta)
        {
            ThrowIfDisposed();
            Game.Instance.AdvanceGameTime(delta);
        }

        /// <summary>
        /// Runs game frames until no battle unit has a command left, and returns the game time that took. Needs
        /// <see cref="BattleScenario.Area"/>. Each frame (1/30 s) runs the game's A* path processing and its own
        /// default-mode controllers in the game's order: unit groups, the frame clock in TimeController's place, line of
        /// sight, movement, entity bounds, vision, sleeping units, memory, combat join, cooldowns, engagement, commands,
        /// buffs, hand equipment, life, combat leave and garbage entities. So while commands approach, start, act and end,
        /// units also see each other, join combat, provoke attacks of opportunity when they leave an enemy's reach, lose
        /// buffs that run out, and fall unconscious or die. AI, ability execution, fog of war, animations and turn-based
        /// combat do not run. Throws <see cref="TimeoutException"/> when
        /// commands are still running after <paramref name="limit"/>, <see cref="InvalidOperationException"/> when a frame
        /// does not advance game time (the time scales pause the game), and fails like <see cref="RuleScope"/> when the
        /// game logs an error, for example an exception it caught.
        /// </summary>
        public TimeSpan RunUntilIdle(TimeSpan limit)
        {
            var elapsed = Frames(limit, () => units.Any(u => !u.Unit.Commands.Empty), null);
            var running = units.Where(u => !u.Unit.Commands.Empty).ToList();
            if (running.Count > 0)
                throw new TimeoutException($"Battle {Scenario.Name}: commands still running after {limit}: "
                    + string.Join(", ", running.Select(u => u.Id + " " + string.Join("+", u.Unit.Commands.Raw.Where(c => c != null).Select(c => c.GetType().Name)))) + ".");
            return elapsed;
        }

        /// <summary>Runs game frames as <see cref="RunUntilIdle"/> does, for a fixed span of game time.</summary>
        public void Run(TimeSpan duration) => Frames(duration, null, null);

        /// <summary>
        /// Runs at least one frame, then more until <paramref name="stop"/> returns true after a frame or
        /// <paramref name="limit"/> of game time has passed; <paramref name="afterFrame"/> sees every frame.
        /// </summary>
        internal TimeSpan RunFrames(TimeSpan limit, Func<bool> stop, Action afterFrame)
        {
            var stopped = false;
            return Frames(limit, () => !stopped, () =>
            {
                afterFrame?.Invoke();
                stopped = stop();
            });
        }

        private TimeSpan Frames(TimeSpan limit, Func<bool> keepGoing, Action afterFrame)
        {
            ThrowIfDisposed();
            if (loop == null)
                throw new InvalidOperationException($"Battle {Scenario.Name} has no area. Set BattleScenario.Area: game frames need the loaded area's settings and navigation graph.");
            var start = Game.Instance.Player.GameTime;
            try
            {
                BattleClock.InFrames = true;
                var allowed = RuleScope.Run("battle frames " + Scenario.Name, () =>
                {
                    // Limits count frame time, the TimeController.DeltaTime the game's controllers advance by. In real time
                    // the game's clock advances by the same amount; in turn-based combat it stands still during a turn,
                    // because the game's CombatController sets it from the time that passed between turns.
                    var frameTime = TimeSpan.Zero;
                    while (frameTime < limit && (keepGoing == null || keepGoing()))
                    {
                        loop.Frame();
                        var time = Game.Instance.TimeController;
                        if (!(BattleClock.TimeScale > 0f))
                            throw new InvalidOperationException(FormattableString.Invariant(
                                $"Battle {Scenario.Name}: the game set its time scale to {BattleClock.TimeScale} (PlayerTimeScale {time.PlayerTimeScale}, DebugTimeScale {time.DebugTimeScale}, UiTimeScale {time.UiTimeScale}). The game is paused at these time scales, so frames would never reach {limit}."));
                        frameTime += TimeSpan.FromSeconds(time.DeltaTime);
                        afterFrame?.Invoke();
                    }
                }, KnownGameLogs.Frames.Concat(Scenario.FrameLogAllowances).ToArray()).Allowed;
                BattleWorker.NoteAllowedFrameLogs(allowed);
            }
            finally
            {
                BattleClock.InFrames = false;
            }
            return Game.Instance.Player.GameTime - start;
        }

        /// <summary>
        /// Cleans up this build and builds the scenario again, with the same seed unless another is given. Throws
        /// <see cref="BattleCleanupException"/> when cleanup left state behind; the process is then dirty.
        /// </summary>
        public void Reset(int? seed = null)
        {
            ThrowIfDisposed();
            var cleanup = Cleanup();
            if (!cleanup.Clean)
            {
                disposed = true;
                BattleWorker.Exit();
                throw new BattleCleanupException(cleanup);
            }
            if (seed.HasValue) Seed = seed.Value;
            try
            {
                Scenario.Validate();
                Build();
            }
            catch
            {
                disposed = true;
                BattleWorker.Exit();
                throw;
            }
        }

        /// <summary>
        /// Cleans up without throwing, so an exception from the test body is never hidden. A dirty cleanup is recorded in
        /// <see cref="LastCleanup"/> and in environment.json, the process refuses further battles and the run fails.
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Cleanup(); }
            finally { BattleWorker.Exit(); }
        }

        private void Build()
        {
            Generation++;
            BattleWorker.NoteBuild();
            baseline = BattleBaseline.Capture();
            outerRandom = GameRandom.Save();
            GameRandom.Init(Seed);
            try
            {
                foreach (var (name, values) in new[] { ("CorpulenceOverrideBySize", Scenario.CorpulenceOverrideBySize), ("CorpulenceFallbackBySize", Scenario.CorpulenceFallbackBySize) })
                    foreach (var pair in values)
                        if (!(pair.Value > 0f) || float.IsInfinity(pair.Value))
                            throw new ArgumentException($"BattleScenario.{name}[Size.{pair.Key}] is {pair.Value}; a corpulence is a positive radius in meters.");
                UnitViewPrefab.Configure(Scenario.CorpulenceOverrideBySize, Scenario.CorpulenceFallbackBySize);
                // Cleanup restores the game time from before the build.
                if (Scenario.StartTime is { } startTime) Game.Instance.Player.GameTime = startTime;
                RuleScope.Run("battle setup " + Scenario.Name, () =>
                {
                    // The difficulty comes first: the game reads it when it builds units and while they fight.
                    if (Scenario.Difficulty != null || Scenario.LimitedAI != null) SetDifficulty();
                    LoadArea();
                    foreach (var spec in Scenario.Units) BuildUnit(spec);
                    if (Scenario.MainCharacter != null) SetMainCharacter(units.First(u => u.Id == Scenario.MainCharacter));
                    foreach (var entry in units.Where(u => Scenario.Companions.Contains(u.Id))) AddCompanion(entry);
                    if (Scenario.TurnBased) SwitchTurnBased();
                    if (navigation != null)
                    {
                        loop = new BattleLoop(navigation);
                        loop.Activate();
                    }
                }, Scenario.SetupLogAllowances.ToArray());
                BattleControl.Set(units.Where(u => Scenario.ControlledSides.Contains(u.Side)).Select(u => u.Unit));
                Paths = Scenario.Paths.Select(path => new BattlePath(path, Find(path.From), Find(path.To))).ToList().AsReadOnly();
                InitialState = BattleState.Capture(this);
            }
            catch (Exception error)
            {
                var cleanup = Cleanup();
                throw new BattleSetupException(Scenario.Name, Generation, error, cleanup);
            }
        }

        private UnitEntityData Find(string id) => id == null ? null : units.First(u => u.Id == id).Unit;

        /// <summary>
        /// Loads the scenario's area as the game's current area, without a scene: an AreaPersistentState of the
        /// blueprint, whose main state then holds the non-party units. Without an area they go to a scene state of
        /// their own, and game frames cannot run.
        /// </summary>
        private void LoadArea()
        {
            if (Scenario.Area == null)
            {
                battleState = new Kingmaker.EntitySystem.SceneEntitiesState(UniqueIdPrefix + Scenario.Name);
                return;
            }
            var blueprint = OfflineGame.Blueprint<Kingmaker.Blueprints.Area.BlueprintArea>(Scenario.Area);
            previousArea = Game.Instance.State.LoadedAreaState;
            previousLoadedArea = LoadedArea.Current;
            area = new Kingmaker.EntitySystem.AreaPersistentState(blueprint);
            Game.Instance.State.LoadedAreaState = area;
            areaLoaded = true;
            // The game's SceneLoader.LoadAreaCoroutine records the area and, without an enter point, the area itself as
            // the current part (NavMeshManager loads that part's graph); code such as BuffCollection.TickBuff reads it.
            new LoadedArea(blueprint, blueprint).MakeCurrent();
            battleState = area.MainState;
            navigation = BattleNavigation.Load(blueprint);
            BattleWorker.NoteNavigation(navigation);
        }

        /// <summary>
        /// Turns on the game's turn-based setting as a player does, before the battle's game mode starts: the game's
        /// CombatController reads it when the mode activates.
        /// </summary>
        /// <summary>Sets the scenario's difficulty settings as a player does in the game's options; cleanup restores them.</summary>
        private void SetDifficulty()
        {
            previousDifficulty = BattleDifficulty.Capture();
            if (Scenario.Difficulty is { } level) BattleDifficulty.Choose(level);
            if (Scenario.LimitedAI is { } limited) BattleDifficulty.SetLimitedAI(limited);
        }

        private void SwitchTurnBased()
        {
            var setting = Kingmaker.Settings.SettingsRoot.Game.TurnBased.EnableTurnBasedMode;
            previousTurnBased = setting.GetValue();
            setting.SetValueAndConfirm(true);
            if (!Game.Instance.Player.IsTurnBasedModeOn())
                throw new InvalidOperationException("The game's turn-based setting is on, but Player.IsTurnBasedModeOn() is false: the player's turn-based state is mandatory and off (Player.IsTurnBasedStateMandatory).");
        }

        private void SetMainCharacter(BattleUnit entry)
        {
            var unit = entry.Unit;
            var player = Game.Instance.Player;
            Require(entry.Spec, unit.Descriptor.IsPlayerFaction, "cannot be the main character: it is not in the player faction");
            // SetMainCharacter points Player.Inventory at the unit's inventory; a player-faction unit without its own
            // inventory already uses the party inventory, so the player's inventory stays the same object.
            Require(entry.Spec, !unit.Descriptor.HasOwnInventory, "cannot be the main character: it has its own inventory instead of the party inventory");
            var inventory = player.Inventory;
            player.SetMainCharacter(unit);
            mainCharacterSet = true;
            Require(entry.Spec, Kingmaker.Designers.GameHelper.GetPlayerCharacter() == unit, "is not the player character after Player.SetMainCharacter");
            Require(entry.Spec, ReferenceEquals(player.Inventory, inventory), "changed the player's inventory when it became the main character");
        }

        private void AddCompanion(BattleUnit entry)
        {
            var unit = entry.Unit;
            Require(entry.Spec, unit.Descriptor.IsPlayerFaction, "cannot join the party: it is not in the player faction");
            Game.Instance.Player.AddCompanion(unit);
            companions.Add(entry);
            Require(entry.Spec, Game.Instance.Player.Party.Contains(unit), "is not in the party after Player.AddCompanion");
            Require(entry.Spec, unit.IsDirectlyControllable, "is in the party, but the game does not let the player control it (UnitEntityData.IsDirectlyControllable)");
        }

        private void BuildUnit(BattleUnitSpec spec)
        {
            var blueprint = OfflineGame.Blueprint<BlueprintUnit>(spec.Blueprint);
            var unit = new UnitEntityData(UniqueIdPrefix + Scenario.Name + ":" + spec.Id, isInGame: false, blueprint);
            units.Add(new BattleUnit(spec, unit));
            if (spec.Faction != null)
            {
                var faction = OfflineGame.Blueprint<BlueprintFaction>(spec.Faction);
                unit.Descriptor.SwitchFactions(faction, resetAttackFactions: true);
                Require(spec, unit.Descriptor.Faction == faction, $"faction is {unit.Descriptor.Faction?.name}, expected {faction.name}");
            }
            foreach (var levels in spec.ClassLevels) AddClassLevels(spec, unit, levels);
            foreach (var stat in spec.BaseStats)
            {
                var value = unit.Stats.GetStat(stat.Key);
                Require(spec, value != null, $"has no stat {stat.Key}");
                value.BaseValue = stat.Value;
                Require(spec, value.BaseValue == stat.Value, $"stat {stat.Key} base is {value.BaseValue}, expected {stat.Value}");
            }
            foreach (var equipment in spec.Equipment) Equip(spec, unit, equipment);
            foreach (var guid in spec.Facts)
            {
                var fact = OfflineGame.Blueprint<Kingmaker.Blueprints.Facts.BlueprintUnitFact>(guid);
                unit.AddFact(fact);
                Require(spec, unit.HasFact(fact), $"does not have fact {fact.name} after AddFact");
            }
            foreach (var guid in spec.Abilities)
            {
                var ability = OfflineGame.Blueprint<BlueprintAbility>(guid);
                unit.AddFact(ability);
                Require(spec, unit.Abilities.GetAbility(ability) != null, $"does not have ability {ability.name} after AddFact");
            }
            foreach (var buff in spec.Buffs)
            {
                var blueprintBuff = OfflineGame.Blueprint<BlueprintBuff>(buff.Buff);
                TimeSpan? duration = buff.Rounds.HasValue ? TimeSpan.FromSeconds(6 * buff.Rounds.Value) : null;
                unit.AddBuff(blueprintBuff, unit, duration);
                Require(spec, unit.Buffs.GetBuff(blueprintBuff) != null, $"buff {blueprintBuff.name} is not active after AddBuff");
            }
            foreach (var resource in spec.Resources) SetResource(spec, unit, resource.Key, resource.Value);
            if (spec.Position is { } point) unit.Position = new UnityEngine.Vector3(point.X, point.Y, point.Z);
            // Commands interrupt themselves when their target is in no scene state (IsInState). The game keeps party
            // units, which use the party inventory, in Player.CrossSceneState; other units belong to an area's state,
            // where the game gives them their own inventory. The battle state stands in for the area.
            var state = unit.Descriptor.IsPlayerFaction && !unit.Descriptor.HasOwnInventory ? Game.Instance.Player.CrossSceneState : battleState;
            state.AddEntityData(unit);
            Require(spec, unit.HoldingState == state, "is not in its scene state after AddEntityData");
            // The units of a loaded area are in the game; the game's unit pools and controllers enumerate only those.
            unit.IsInGame = true;
            OfflineUnitView.Attach(unit);
        }

        private static void AddClassLevels(BattleUnitSpec spec, UnitEntityData unit, BattleClassLevels levels)
        {
            var characterClass = OfflineGame.Blueprint<BlueprintCharacterClass>(levels.CharacterClass);
            var before = unit.Descriptor.Progression.GetClassLevel(characterClass);
            var component = new AddClassLevels { CharacterClass = characterClass, Levels = levels.Levels };
            Kingmaker.Blueprints.Classes.AddClassLevels.LevelUp(component, unit.Descriptor, levels.Levels);
            var after = unit.Descriptor.Progression.GetClassLevel(characterClass);
            var limit = unit.Descriptor.OriginalBlueprint.GetComponent<ClassLevelLimit>();
            Require(spec, after == before + levels.Levels,
                $"{characterClass.name} level is {after}, expected {before + levels.Levels}"
                + (limit == null ? "" : $"; the blueprint's ClassLevelLimit is {limit.LevelLimit}, so the game records further levels as a level-up plan"));
        }

        private static void Equip(BattleUnitSpec spec, UnitEntityData unit, BattleEquipment equipment)
        {
            var blueprint = OfflineGame.Blueprint<Kingmaker.Blueprints.Items.BlueprintItem>(equipment.Item);
            var slot = Slot(spec, unit, equipment.Slot);
            var item = blueprint.CreateEntity();
            if (!slot.CanInsertItem(item))
            {
                var reasons = new List<string>();
                if (!slot.IsItemSupported(item)) reasons.Add($"{slot.GetType().Name} does not take this item");
                if (!item.CanBeEquippedBy(unit.Descriptor))
                    reasons.Add(unit.Descriptor.IsPlayerFaction
                        ? "the game's CanBeEquippedBy refuses it for this player-faction unit (proficiency or equipment restriction)"
                        : "the game's CanBeEquippedBy refuses it");
                if (slot.HasItem && !slot.CanRemoveItem()) reasons.Add($"the slot's current item {slot.MaybeItem?.Blueprint?.name} cannot be removed");
                item.Dispose();
                throw new BattleSetupException(spec.Id, $"cannot equip {blueprint.name} in {equipment.Slot}: {(reasons.Count == 0 ? "the game's CanInsertItem returned false" : string.Join("; ", reasons))}.");
            }
            slot.InsertItem(item);
            Require(spec, slot.MaybeItem?.Blueprint == blueprint, $"{equipment.Slot} holds {slot.MaybeItem?.Blueprint?.name ?? "nothing"} after inserting {blueprint.name}");
        }

        private static ItemSlot Slot(BattleUnitSpec spec, UnitEntityData unit, BattleSlot slot)
        {
            var body = unit.Body;
            return slot switch
            {
                BattleSlot.PrimaryHand => body.PrimaryHand,
                BattleSlot.SecondaryHand => body.SecondaryHand,
                BattleSlot.Armor => body.Armor,
                BattleSlot.Shirt => body.Shirt,
                BattleSlot.Belt => body.Belt,
                BattleSlot.Head => body.Head,
                BattleSlot.Glasses => body.Glasses,
                BattleSlot.Feet => body.Feet,
                BattleSlot.Gloves => body.Gloves,
                BattleSlot.Neck => body.Neck,
                BattleSlot.Ring1 => body.Ring1,
                BattleSlot.Ring2 => body.Ring2,
                BattleSlot.Wrist => body.Wrist,
                BattleSlot.Shoulders => body.Shoulders,
                >= BattleSlot.QuickSlot1 and <= BattleSlot.QuickSlot5 => slot - BattleSlot.QuickSlot1 < body.QuickSlots.Length
                    ? body.QuickSlots[slot - BattleSlot.QuickSlot1]
                    : throw new BattleSetupException(spec.Id, $"the unit's body has {body.QuickSlots.Length} quick slots, not {slot}"),
                _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
            };
        }

        private static void SetResource(BattleUnitSpec spec, UnitEntityData unit, string guid, int amount)
        {
            var resource = OfflineGame.Blueprint<BlueprintAbilityResource>(guid);
            var resources = unit.Descriptor.Resources;
            Require(spec, resources.ContainsResource(resource), $"has no resource {resource.name}; add the fact that grants it");
            resources.Restore(resource);
            var maximum = resources.GetResourceAmount(resource);
            Require(spec, amount >= 0 && amount <= maximum, $"resource {resource.name} amount {amount} is outside 0..{maximum}");
            if (amount < maximum) resources.Spend(resource, maximum - amount);
            Require(spec, resources.GetResourceAmount(resource) == amount, $"resource {resource.name} is {resources.GetResourceAmount(resource)}, expected {amount}");
        }

        private static void Require(BattleUnitSpec spec, bool condition, string problem)
        {
            if (!condition) throw new BattleSetupException(spec.Id, problem + ".");
        }

        /// <summary>
        /// What SelectionCharacterController.OnAreaBeginUnloading clears: its group of the moment (built by its Tick, which a
        /// battle does not run), the selected units, the selected unit and the full-screen selection.
        /// </summary>
        private static void ClearSelection()
        {
            var selection = Game.Instance.SelectionCharacter;
            var type = typeof(Kingmaker.Controllers.SelectionCharacterController);
            (HarmonyLib.AccessTools.Field(type, "m_ActualGroup").GetValue(selection) as System.Collections.IList)?.Clear();
            selection.SelectedUnits.Clear();
            var property = HarmonyLib.AccessTools.Field(type, "SelectedUnit").GetValue(selection);
            var value = property.GetType().GetProperty("Value");
            value.SetValue(property, value.PropertyType.IsValueType ? Activator.CreateInstance(value.PropertyType) : null);
            HarmonyLib.AccessTools.Field(type, "m_FullScreenSelectedUnit").SetValue(selection, null);
        }

        private BattleCleanup Cleanup()
        {
            var failures = new List<string>();
            var logStart = HarnessState.Logs.Count;
            void Step(string name, Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(name + ": " + error); }
            }

            foreach (var subscriber in subscriptions)
                Step("unsubscribe " + subscriber.GetType().FullName, () => EventBus.Unsubscribe(subscriber));
            subscriptions.Clear();
            if (loop != null)
            {
                // Before the units go: the pointer moves off the unit it is on, and the player's selection is cleared as the
                // game's SelectionCharacterController clears it when an area begins unloading. Both would otherwise keep
                // units of this battle for the next one.
                Step("move the pointer off the battle's units", BattlePointer.Leave);
                Step("clear the player's selection as an unloading area does", ClearSelection);
            }
            // While the units are still whole, as the game's Player.RemoveCompanion expects; they stay in the game until
            // they are destroyed below.
            foreach (var companion in companions)
                Step("remove " + companion.Id + " from the party", () => Game.Instance.Player.RemoveCompanion(companion.Unit, stayInGame: true));
            companions.Clear();
            for (var i = units.Count - 1; i >= 0; i--)
            {
                var unit = units[i].Unit;
                var view = unit.View as Kingmaker.View.UnitEntityView;
                // The steps EntityDestructionController takes for entities the game removes; like the game, cleanup does
                // not interrupt unfinished commands first. Dispose leaves combat with the view still attached, then
                // detaches the view and destroys its object, which destroys the stand-in.
                Step("destroy " + units[i].Id, unit.Destroy);
                Step("dispose " + units[i].Id, unit.Dispose);
                Step("release the view stand-in of " + units[i].Id, () => OfflineUnitView.Release(unit, view));
                Step("remove " + units[i].Id + " from its scene state", () => unit.HoldingState?.RemoveEntityData(unit));
                Step("remove " + units[i].Id + " from the awake units", () => Game.Instance.State.AwakeUnits.Remove(unit));
                Step("report the destruction of " + units[i].Id,
                    () => EventBus.RaiseEvent((Kingmaker.PubSubSystem.IEntityDestructionHandler h) => h.HandleEntityDestruction(unit)));
            }
            BattleControl.Clear();
            battleState = null;
            // After the units left their scene states, or the game's UpdateCharacterLists would put the main character back
            // into the party; before anything reads the party, because SetMainCharacter refreshes the cached lists.
            if (mainCharacterSet && baseline != null)
                Step("restore the player's main character", baseline.RestoreMainCharacter);
            mainCharacterSet = false;
            if (loop != null)
            {
                var running = loop;
                Step("stop the battle's game controllers", running.Deactivate);
                loop = null;
            }
            // UnitCombatJoinController updates the player's combat state every frame; after the units left, so does cleanup.
            Step("update the player's combat state", () => Game.Instance.Player.UpdateIsInCombat());
            if (previousTurnBased is { } turnBased)
            {
                Step("restore the game's turn-based setting", () => Kingmaker.Settings.SettingsRoot.Game.TurnBased.EnableTurnBasedMode.SetValueAndConfirm(turnBased));
                previousTurnBased = null;
            }
            if (previousDifficulty != null)
            {
                Step("restore the game's difficulty settings", previousDifficulty.Restore);
                previousDifficulty = null;
            }
            if (navigation != null)
            {
                var loaded = navigation;
                Step("unload the area's navigation graph", loaded.Unload);
                navigation = null;
            }
            if (area != null)
            {
                var loaded = area;
                Step("dispose the battle area", loaded.Dispose);
                area = null;
            }
            if (areaLoaded)
            {
                Step("restore the loaded area", () => Game.Instance.State.LoadedAreaState = previousArea);
                var loadedArea = previousLoadedArea;
                Step("restore the scene loader's current area", () => loadedArea.MakeCurrent());
                areaLoaded = false;
                previousArea = null;
                previousLoadedArea = null;
            }
            units.Clear();
            Paths = Array.Empty<BattlePath>();
            if (baseline != null)
            {
                Step("remove items added to the player's shared inventory", baseline.RemoveAddedPlayerItems);
                Step("restore game time", baseline.RestoreGameTime);
                Step("restore the frame clock", baseline.RestoreFrameClock);
            }
            foreach (var guid in temporaryBlueprints)
                Step("remove temporary blueprint " + guid, () => ResourcesLibrary.BlueprintsCache.RemoveCachedBlueprint(guid));
            var removedBlueprints = temporaryBlueprints.ToList();
            temporaryBlueprints.Clear();
            if (outerRandom != null) Step("restore game RNG", () => GameRandom.Restore(outerRandom));
            outerRandom = null;
            UnitViewPrefab.ClearConfigured();
            // An error the game catches and logs during cleanup, for example in a destruction handler, is a failed step:
            // the step returned, but the game did not finish what it was doing.
            foreach (var log in HarnessState.Logs.ToArray().Skip(logStart))
                if (RuleScope.IsError(log)) failures.Add("the game logged an error during cleanup: " + RuleSettlementException.Describe(log));

            var residue = new List<string>();
            if (baseline != null)
            {
                try { residue.AddRange(baseline.Compare(BattleBaseline.Capture())); }
                catch (Exception error) { failures.Add("residue check: " + error); }
            }
            foreach (var guid in removedBlueprints)
                if (ResourcesLibrary.TryGetBlueprint(guid) != null) residue.Add($"temporary blueprint {guid} is still in the blueprint cache");
            baseline = null;
            LastCleanup = new BattleCleanup(Scenario.Name, Generation, Seed, failures, residue);
            BattleWorker.Record(LastCleanup);
            return LastCleanup;
        }

        private string Describe() => Scenario.Name;

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(OfflineBattle), $"Battle {Scenario.Name} was disposed.");
        }
    }

    /// <summary>The area and area part the game's SceneLoader records as loaded (Game.CurrentlyLoadedArea and its part).</summary>
    internal sealed class LoadedArea
    {
        private static readonly System.Reflection.FieldInfo SceneLoader = HarmonyLib.AccessTools.Field(typeof(Game), "m_SceneLoader");
        private static readonly System.Reflection.MethodInfo SetArea = HarmonyLib.AccessTools.PropertySetter(
            typeof(Kingmaker.EntitySystem.Persistence.Scenes.SceneLoader), nameof(Kingmaker.EntitySystem.Persistence.Scenes.SceneLoader.CurrentlyLoadedArea));
        private static readonly System.Reflection.MethodInfo SetPart = HarmonyLib.AccessTools.PropertySetter(
            typeof(Kingmaker.EntitySystem.Persistence.Scenes.SceneLoader), nameof(Kingmaker.EntitySystem.Persistence.Scenes.SceneLoader.CurrentlyLoadedAreaPart));

        internal LoadedArea(Kingmaker.Blueprints.Area.BlueprintArea area, Kingmaker.Blueprints.Area.BlueprintAreaPart part)
        {
            Area = area;
            Part = part;
        }

        internal Kingmaker.Blueprints.Area.BlueprintArea Area { get; }
        internal Kingmaker.Blueprints.Area.BlueprintAreaPart Part { get; }

        internal static LoadedArea Current => new(Game.Instance.CurrentlyLoadedArea, Game.Instance.CurrentlyLoadedAreaPart);

        internal void MakeCurrent()
        {
            var loader = SceneLoader.GetValue(Game.Instance);
            SetArea.Invoke(loader, new object[] { Area });
            SetPart.Invoke(loader, new object[] { Part });
        }
    }

    /// <summary>A unit of the current build, with the scenario entry it was built from.</summary>
    public sealed class BattleUnit
    {
        internal BattleUnit(BattleUnitSpec spec, UnitEntityData unit)
        {
            Spec = spec;
            Unit = unit;
        }

        public BattleUnitSpec Spec { get; }
        public string Id => Spec.Id;
        public string Side => Spec.Side;
        public UnitEntityData Unit { get; }

        /// <summary>True when the unit's side is controlled: the game's AI does not decide for it.</summary>
        public bool Controlled => BattleControl.IsControlled(Unit);
    }

    /// <summary>A scenario path as a decision maker observes it: the supplied data plus the current units at its ends.</summary>
    public sealed class BattlePath
    {
        internal BattlePath(BattlePathSpec spec, UnitEntityData from, UnitEntityData to)
        {
            Spec = spec;
            From = from;
            To = to;
        }

        public BattlePathSpec Spec { get; }
        public string Id => Spec.Id;
        public string Source => Spec.Source;
        public IReadOnlyList<BattlePoint> Points => Spec.Points;
        public double? Distance => Spec.Distance;
        public double? Cost => Spec.Cost;
        public string Units => Spec.Units;
        public string FromId => Spec.From;
        public string ToId => Spec.To;

        /// <summary>The unit of this build at the start of the path, or null.</summary>
        public UnitEntityData From { get; }

        /// <summary>The unit of this build at the end of the path, or null.</summary>
        public UnitEntityData To { get; }
    }

    /// <summary>
    /// Key state of a battle as sorted text lines: per unit its blueprint, faction, levels, hit points, every stat,
    /// equipment, buffs, facts, abilities, resources and position, plus game time and the game RNG. Unique ids are left
    /// out. Two builds of one scenario with one seed must produce equal states.
    /// </summary>
    public sealed class BattleState : IEquatable<BattleState>
    {
        private BattleState(IReadOnlyList<string> lines) => Lines = lines;

        public IReadOnlyList<string> Lines { get; }

        internal static BattleState Capture(OfflineBattle battle)
        {
            var player = Game.Instance.Player;
            var lines = new List<string>
            {
                "game-time " + player.GameTime.ToString("c", CultureInfo.InvariantCulture),
                "game-random seed " + GameRandom.Seed + " calls " + GameRandom.CallsSinceInit,
                "player-inventory " + Items(player.Inventory),
                "main-character " + (battle.Units.FirstOrDefault(u => u.Unit == player.MainCharacter.Value)?.Id ?? (player.MainCharacter.Value == null ? "none" : "outside the battle")),
                "party " + player.PartyCharacters.Count,
            };
            foreach (var entry in battle.Units)
            {
                var unit = entry.Unit;
                var descriptor = unit.Descriptor;
                var prefix = "unit " + entry.Id + " ";
                lines.Add(prefix + "side " + entry.Side);
                lines.Add(prefix + "control " + (entry.Controlled ? "controlled" : "game-ai"));
                lines.Add(prefix + "blueprint " + unit.Blueprint.name);
                lines.Add(prefix + "faction " + descriptor.Faction?.name);
                lines.Add(prefix + "level " + descriptor.Progression.CharacterLevel + " mythic " + descriptor.Progression.MythicLevel);
                lines.Add(prefix + "classes " + string.Join(",", descriptor.Progression.Classes.Select(c => c.CharacterClass.name + ":" + c.Level).OrderBy(x => x, StringComparer.Ordinal)));
                lines.Add(prefix + "hit-points " + unit.HPLeft + "/" + descriptor.MaxHP + " damage " + descriptor.Damage + " temporary " + descriptor.TemporaryHP);
                foreach (StatType stat in Enum.GetValues(typeof(StatType)).Cast<StatType>().Distinct())
                {
                    var value = unit.Stats.GetStat(stat);
                    if (value != null) lines.Add(prefix + "stat " + stat + " " + value.ModifiedValue + " base " + value.BaseValue);
                }
                foreach (BattleSlot slot in Enum.GetValues(typeof(BattleSlot)))
                    lines.Add(prefix + "slot " + slot + " " + (SlotItem(unit, slot) ?? "-"));
                // Player-faction units without their own inventory share the party inventory, listed once above.
                lines.Add(prefix + "inventory " + (descriptor.HasOwnInventory ? Items(descriptor.Inventory) : "party"));
                lines.Add(prefix + "buffs " + string.Join(",", unit.Buffs.RawFacts.Select(b => b.Blueprint.name + ":" + b.GetRank()).OrderBy(x => x, StringComparer.Ordinal)));
                lines.Add(prefix + "facts " + string.Join(",", unit.Facts.List.Select(f => f.Blueprint?.name ?? f.GetType().Name).OrderBy(x => x, StringComparer.Ordinal)));
                lines.Add(prefix + "abilities " + string.Join(",", unit.Abilities.RawFacts.Select(a => a.Blueprint.name).OrderBy(x => x, StringComparer.Ordinal)));
                var resources = new List<string>();
                foreach (var resource in descriptor.Resources) resources.Add(resource.name + ":" + descriptor.Resources.GetResourceAmount(resource));
                resources.Sort(StringComparer.Ordinal);
                lines.Add(prefix + "resources " + string.Join(",", resources));
                lines.Add(prefix + "position " + new BattlePoint(unit.Position.x, unit.Position.y, unit.Position.z));
                lines.Add(prefix + "commands " + (unit.Commands.Empty ? "none" : "running"));
                lines.Add(prefix + "combat " + (unit.IsInCombat ? "in" : "out"));
            }
            return new BattleState(lines.AsReadOnly());
        }

        private static string Items(ItemsCollection inventory)
            => inventory == null ? "none"
                : string.Join(",", inventory.Items.Select(i => i.Blueprint.name + "x" + i.Count).OrderBy(x => x, StringComparer.Ordinal));

        private static string SlotItem(UnitEntityData unit, BattleSlot slot)
        {
            var body = unit.Body;
            ItemSlot itemSlot = slot switch
            {
                BattleSlot.PrimaryHand => body.PrimaryHand,
                BattleSlot.SecondaryHand => body.SecondaryHand,
                BattleSlot.Armor => body.Armor,
                BattleSlot.Shirt => body.Shirt,
                BattleSlot.Belt => body.Belt,
                BattleSlot.Head => body.Head,
                BattleSlot.Glasses => body.Glasses,
                BattleSlot.Feet => body.Feet,
                BattleSlot.Gloves => body.Gloves,
                BattleSlot.Neck => body.Neck,
                BattleSlot.Ring1 => body.Ring1,
                BattleSlot.Ring2 => body.Ring2,
                BattleSlot.Wrist => body.Wrist,
                BattleSlot.Shoulders => body.Shoulders,
                _ => null,
            };
            return itemSlot?.MaybeItem?.Blueprint?.name;
        }

        /// <summary>Lines present in only one of the two states, prefixed with - (this state) or + (other).</summary>
        public IReadOnlyList<string> Difference(BattleState other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            var removed = Lines.Except(other.Lines).Select(line => "- " + line);
            var added = other.Lines.Except(Lines).Select(line => "+ " + line);
            return removed.Concat(added).ToList();
        }

        public bool Equals(BattleState other) => other != null && Lines.SequenceEqual(other.Lines, StringComparer.Ordinal);
        public override bool Equals(object obj) => Equals(obj as BattleState);
        public override int GetHashCode() => Lines.Count == 0 ? 0 : StringComparer.Ordinal.GetHashCode(Lines[Lines.Count - 1]) ^ Lines.Count;
        public override string ToString() => string.Join(Environment.NewLine, Lines);
    }

    /// <summary>What a cleanup did not manage: failed steps and game-wide state that differs from before the build.</summary>
    public sealed class BattleCleanup
    {
        internal BattleCleanup(string scenario, int generation, int seed, IReadOnlyList<string> failures, IReadOnlyList<string> residue)
        {
            Scenario = scenario;
            Generation = generation;
            Seed = seed;
            Failures = failures;
            Residue = residue;
        }

        public string Scenario { get; }
        public int Generation { get; }
        public int Seed { get; }
        public IReadOnlyList<string> Failures { get; }
        public IReadOnlyList<string> Residue { get; }
        public bool Clean => Failures.Count == 0 && Residue.Count == 0;

        public override string ToString()
            => Clean ? $"{Scenario} #{Generation}: clean"
                : $"{Scenario} #{Generation}: " + string.Join("; ", Failures.Concat(Residue));
    }

    /// <summary>A battle could not be built as its scenario describes. What the build created was cleaned up first.</summary>
    public sealed class BattleSetupException : Exception
    {
        internal BattleSetupException(string unit, string problem)
            : base($"Battle unit {unit}: {problem}")
        {
        }

        internal BattleSetupException(string scenario, int generation, Exception inner, BattleCleanup cleanup)
            : base($"Battle {scenario} build #{generation} failed: {inner.Message}" + (cleanup.Clean ? "" : $" Cleanup after the failure was not clean, the process is dirty: {cleanup}"), inner)
        {
            Cleanup = cleanup;
        }

        /// <summary>The cleanup that ran after the failed build.</summary>
        public BattleCleanup Cleanup { get; }
    }

    /// <summary>Cleanup left state behind. The process is marked dirty and refuses further battles.</summary>
    public sealed class BattleCleanupException : Exception
    {
        internal BattleCleanupException(BattleCleanup cleanup)
            : base(BattleWorker.DirtyMarker + " Battle cleanup was not clean: " + cleanup)
        {
            Cleanup = cleanup;
        }

        public BattleCleanup Cleanup { get; }
    }
}
