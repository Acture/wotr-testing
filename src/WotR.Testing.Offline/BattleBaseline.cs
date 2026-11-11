using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.PubSubSystem;
using Kingmaker.RuleSystem;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Game-wide state a battle can leave behind, captured before a build and compared after its cleanup. Every entry is
    /// read from the game's own registries; nothing here changes them except the two restore steps a battle owns.
    /// </summary>
    internal sealed class BattleBaseline
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private List<object> globalSubscribers;
        private List<object> unitSubscriptions;
        private List<object> rulebookGlobalSubscribers;
        private List<object> rulebookInitiatorSubscriptions;
        private List<object> rulebookTargetSubscriptions;
        private List<object> entities;
        private List<object> stateUnits;
        private List<object> awakeUnits;
        private List<object> crossSceneEntities;
        private List<object> playerItems;
        private Dictionary<object, int> playerItemCounts;
        private TimeSpan gameTime;
        private float frameDelta;
        private float gameFrameDelta;
        private int standInViews;
        private bool playerInCombat;
        private int partyCount;
        private object mainCharacter;
        private object playerInventory;
        private int unitGroups;
        private object loadedArea;
        private object sceneArea;
        private object scenePart;
        private object astar;
        private int boundsScene;
        private object levelUpController;
        private object currentTurn;
        private bool turnBasedSetting;
        private bool turnBasedInitialized;
        private bool paused;
        private object loadingProcess;
        private object dispatcher;
        private object pathVisualizer;
        private object pointerOn;

        /// <summary>SelectionCharacterController.SelectedUnit, a UniRx reactive property of a UnitReference, read by reflection.</summary>
        private static object SelectedUnit()
        {
            var property = HarmonyLib.AccessTools.Field(typeof(Kingmaker.Controllers.SelectionCharacterController), "SelectedUnit").GetValue(Game.Instance.SelectionCharacter);
            var reference = property?.GetType().GetProperty("Value")?.GetValue(property);
            return reference?.GetType().GetProperty("Value")?.GetValue(reference);
        }
        private object[] selectedUnits;
        private object selectedUnit;
        private object selectionManager;
        private object clickEvents;
        private int detachedStandIns;
        private int ruleStack;
        private int randomSeed;
        private long randomCalls;

        /// <summary>
        /// The player context battle units need, prepared once per process with the game's own calls. Player-faction units
        /// without their own inventory keep items in the party inventory, which the game creates on demand.
        /// </summary>
        internal static void PreparePlayerContext()
        {
            var player = Game.Instance.Player
                ?? throw new OfflineInitializationException("battle-player-context", "Game.Instance.Player is null; battles need the game's player object.");
            if (player.Inventory == null)
            {
                player.EnsureInventory();
                BattleWorker.NotePlayerContext("shared-inventory",
                    "Player.EnsureInventory(): the party inventory that player-faction units without their own inventory use for starting items.");
            }
            if (player.Get<Kingmaker.Formations.PartyFormationManager>() == null)
            {
                _ = player.FormationManager;
                BattleWorker.NotePlayerContext("party-formation",
                    "Player.FormationManager: the party formation the game's click handlers read when the player moves or attacks; the player creates it on first use and keeps it for the session.");
            }
        }

        private static readonly FieldInfo DefaultUnitField = typeof(Game).GetField("m_DefaultUnit", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Game.m_DefaultUnit is not readable on this game version.");

        /// <summary>
        /// The game's DefaultUnit and the items it holds. The game creates it the first time a calculation needs a unit
        /// and has none (for example the stats of a weapon nobody wields) and keeps it for the session without disposing
        /// it, so its appearance during a battle is not state the battle left behind.
        /// </summary>
        private static bool IsSessionObject(object value, Kingmaker.EntitySystem.Entities.UnitEntityData defaultUnit)
            => defaultUnit != null && (ReferenceEquals(value, defaultUnit)
                || value is Kingmaker.Items.ItemEntity item && (ReferenceEquals(item.Owner?.Unit, defaultUnit) || ReferenceEquals(item.Wielder?.Unit, defaultUnit))
                || value is UnitSubscription subscription && (IsSessionObject(subscription.Unit, defaultUnit) || IsSessionObject(subscription.Subscriber, defaultUnit)));

        private static List<object> WithoutSessionObjects(List<object> values)
        {
            var defaultUnit = DefaultUnitField.GetValue(Game.Instance) as Kingmaker.EntitySystem.Entities.UnitEntityData;
            values.RemoveAll(value => IsSessionObject(value, defaultUnit));
            return values;
        }

        internal static BattleBaseline Capture()
        {
            var player = Game.Instance.Player;
            return new BattleBaseline
            {
                globalSubscribers = WithoutSessionObjects(Subscribers(EventBus.GlobalSubscribers)),
                unitSubscriptions = WithoutSessionObjects(UnitSubscriptions(EventBus.UnitSubscribers)),
                rulebookGlobalSubscribers = WithoutSessionObjects(Subscribers(RulebookEventBus.GlobalRulebookSubscribers)),
                rulebookInitiatorSubscriptions = WithoutSessionObjects(UnitSubscriptions(RulebookEventBus.InitiatorRulebookSubscribers)),
                rulebookTargetSubscriptions = WithoutSessionObjects(UnitSubscriptions(RulebookEventBus.TargetRulebookSubscribers)),
                entities = WithoutSessionObjects(EntityService.Instance?.GetListUnsafe().Cast<object>().ToList() ?? new List<object>()),
                stateUnits = Game.Instance.State.Units.All.Cast<object>().ToList(),
                awakeUnits = Game.Instance.State.AwakeUnits.Cast<object>().ToList(),
                crossSceneEntities = player.CrossSceneState.AllEntityData.Cast<object>().ToList(),
                playerItems = player.Inventory?.Items.Cast<object>().ToList() ?? new List<object>(),
                playerItemCounts = (player.Inventory?.Items ?? Enumerable.Empty<Kingmaker.Items.ItemEntity>())
                    .ToDictionary(item => (object)item, item => item.Count, ReferenceComparer.Instance),
                gameTime = player.GameTime,
                frameDelta = Game.Instance.TimeController.DeltaTime,
                gameFrameDelta = Game.Instance.TimeController.GameDeltaTime,
                standInViews = OfflineUnitView.Count,
                playerInCombat = player.IsInCombat,
                partyCount = player.PartyCharacters.Count,
                mainCharacter = player.MainCharacter.Value,
                playerInventory = player.Inventory,
                unitGroups = Game.Instance.UnitGroups.Count,
                loadedArea = Game.Instance.State.LoadedAreaState,
                sceneArea = Game.Instance.CurrentlyLoadedArea,
                scenePart = Game.Instance.CurrentlyLoadedAreaPart,
                astar = AstarPath.active,
                boundsScene = Game.Instance.EntityBoundsController.Scene.handle,
                levelUpController = Game.Instance.LevelUpController,
                currentTurn = Game.Instance.TurnBasedCombatController?.CurrentTurn,
                turnBasedSetting = Kingmaker.Settings.SettingsRoot.Game.TurnBased.EnableTurnBasedMode.GetValue(),
                turnBasedInitialized = Game.Instance.TurnBasedCombatController?.Initialized ?? false,
                paused = Game.Instance.IsPaused,
                loadingProcess = LoadingProcessStandIn.Current,
                dispatcher = MainThreadDispatcherStandIn.Current,
                pathVisualizer = PathVisualizerStandIn.Current,
                pointerOn = Game.Instance.DefaultPointerController?.PointerOn,
                selectedUnits = Game.Instance.SelectionCharacter.SelectedUnits.Cast<object>().ToArray(),
                selectedUnit = SelectedUnit(),
                selectionManager = SelectionManagerStandIn.Current,
                clickEvents = Game.Instance.ClickEventsController,
                detachedStandIns = OfflineUnitView.DetachedCount,
                ruleStack = Rulebook.CurrentContext.StackSize,
                randomSeed = GameRandom.Seed,
                randomCalls = GameRandom.CallsSinceInit,
            };
        }

        /// <summary>Items the build put into the player's party inventory are removed and disposed.</summary>
        internal void RemoveAddedPlayerItems()
        {
            var inventory = Game.Instance.Player.Inventory;
            if (inventory == null) return;
            var before = new HashSet<object>(playerItems, ReferenceComparer.Instance);
            foreach (var item in inventory.Items.Where(i => !before.Contains(i)).ToList())
            {
                inventory.Remove(item);
                item.Dispose();
            }
        }

        internal void RestoreGameTime() => Game.Instance.Player.GameTime = gameTime;

        /// <summary>Puts back the frame durations the game's TimeController reported before the build.</summary>
        internal void RestoreFrameClock() => BattleClock.Set(frameDelta, gameFrameDelta);

        /// <summary>Puts back the main character from before the build with the game's own <c>Player.SetMainCharacter</c>.</summary>
        internal void RestoreMainCharacter() => Game.Instance.Player.SetMainCharacter((Kingmaker.EntitySystem.Entities.UnitEntityData)mainCharacter);

        internal IEnumerable<string> Compare(BattleBaseline after)
        {
            var residue = new List<string>();
            Difference(residue, "EventBus global subscribers", globalSubscribers, after.globalSubscribers);
            Difference(residue, "EventBus unit subscriptions", unitSubscriptions, after.unitSubscriptions);
            Difference(residue, "Rulebook global subscribers", rulebookGlobalSubscribers, after.rulebookGlobalSubscribers);
            Difference(residue, "Rulebook initiator subscriptions", rulebookInitiatorSubscriptions, after.rulebookInitiatorSubscriptions);
            Difference(residue, "Rulebook target subscriptions", rulebookTargetSubscriptions, after.rulebookTargetSubscriptions);
            Difference(residue, "EntityService entities", entities, after.entities);
            Difference(residue, "game state units", stateUnits, after.stateUnits);
            Difference(residue, "game awake units", awakeUnits, after.awakeUnits);
            Difference(residue, "Player.CrossSceneState entities", crossSceneEntities, after.crossSceneEntities);
            Difference(residue, "player party inventory items", playerItems, after.playerItems);
            // A stack the build added to keeps its identity; only its count shows the change.
            foreach (var pair in playerItemCounts)
                if (after.playerItemCounts.TryGetValue(pair.Key, out var count) && count != pair.Value)
                    residue.Add($"player party inventory item {Name(pair.Key)}: count {pair.Value} before, {count} after");
            Value(residue, "game time", gameTime.ToString("c", CultureInfo.InvariantCulture), after.gameTime.ToString("c", CultureInfo.InvariantCulture));
            Value(residue, "frame delta time", frameDelta, after.frameDelta);
            Value(residue, "game frame delta time", gameFrameDelta, after.gameFrameDelta);
            Value(residue, "battle unit view stand-ins", standInViews, after.standInViews);
            Value(residue, "player in combat", playerInCombat, after.playerInCombat);
            Value(residue, "player party size", partyCount, after.partyCount);
            if (!ReferenceEquals(mainCharacter, after.mainCharacter)) residue.Add($"player main character changed: {mainCharacter ?? "none"} -> {after.mainCharacter ?? "none"}");
            if (!ReferenceEquals(playerInventory, after.playerInventory)) residue.Add("Player.Inventory is a different collection than before the build");
            Value(residue, "game unit groups", unitGroups, after.unitGroups);
            if (!ReferenceEquals(loadedArea, after.loadedArea)) residue.Add("Game.Instance.State.LoadedAreaState changed");
            if (!ReferenceEquals(sceneArea, after.sceneArea) || !ReferenceEquals(scenePart, after.scenePart))
                residue.Add($"the scene loader's current area changed from {sceneArea ?? "none"}/{scenePart ?? "none"} to {after.sceneArea ?? "none"}/{after.scenePart ?? "none"}");
            if (!ReferenceEquals(astar, after.astar)) residue.Add("AstarPath.active changed");
            if (boundsScene != after.boundsScene) residue.Add($"EntityBoundsController scene handle changed from {boundsScene} to {after.boundsScene}");
            if (!ReferenceEquals(levelUpController, after.levelUpController)) residue.Add("Game.Instance.LevelUpController changed");
            if (!ReferenceEquals(currentTurn, after.currentTurn)) residue.Add("the turn-based combat controller's current turn changed");
            Value(residue, "the game's turn-based setting", turnBasedSetting, after.turnBasedSetting);
            Value(residue, "turn-based combat initialized", turnBasedInitialized, after.turnBasedInitialized);
            Value(residue, "game paused", paused, after.paused);
            if (!ReferenceEquals(loadingProcess, after.loadingProcess)) residue.Add("LoadingProcess.Instance changed");
            if (!ReferenceEquals(dispatcher, after.dispatcher)) residue.Add("UniRx MainThreadDispatcher instance changed");
            if (!ReferenceEquals(pathVisualizer, after.pathVisualizer)) residue.Add("PathVisualizer.Instance changed");
            if (!ReferenceEquals(pointerOn, after.pointerOn)) residue.Add("the pointer hovers another object (DefaultPointerController.PointerOn)");
            if (!selectedUnits.SequenceEqual(after.selectedUnits) || !ReferenceEquals(selectedUnit, after.selectedUnit))
                residue.Add($"the player's selection holds other units ({after.selectedUnits.Length} selected)");
            if (!ReferenceEquals(selectionManager, after.selectionManager)) residue.Add("SelectionManagerBase.Instance changed");
            if (!ReferenceEquals(clickEvents, after.clickEvents)) residue.Add("Game.ClickEventsController changed");
            Value(residue, "presentation stand-ins", detachedStandIns, after.detachedStandIns);
            Value(residue, "rulebook event stack", ruleStack, after.ruleStack);
            Value(residue, "game RNG seed", randomSeed, after.randomSeed);
            Value(residue, "game RNG calls since init", randomCalls, after.randomCalls);
            return residue;
        }

        private static void Value<T>(List<string> residue, string name, T before, T after)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after)) residue.Add($"{name}: {before} before, {after} after");
        }

        private static void Difference(List<string> residue, string name, List<object> before, List<object> after)
        {
            var counts = new Dictionary<object, int>(EntryComparer.Instance);
            foreach (var item in after) counts[item] = counts.TryGetValue(item, out var count) ? count + 1 : 1;
            foreach (var item in before) counts[item] = counts.TryGetValue(item, out var count) ? count - 1 : -1;
            var added = counts.Where(pair => pair.Value > 0).SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).ToList();
            var removed = counts.Where(pair => pair.Value < 0).SelectMany(pair => Enumerable.Repeat(pair.Key, -pair.Value)).ToList();
            if (added.Count > 0) residue.Add($"{name}: {added.Count} left behind ({Describe(added)})");
            if (removed.Count > 0) residue.Add($"{name}: {removed.Count} missing that existed before ({Describe(removed)})");
        }

        private static string Describe(IEnumerable<object> items)
            => string.Join(", ", items.GroupBy(item => Name(item)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() == 1 ? g.Key : g.Key + " x" + g.Count()));

        private static string Name(object item)
        {
            if (item is UnitSubscription subscription) return Name(subscription.Subscriber) + " for unit " + Name(subscription.Unit);
            string text;
            try { text = item.ToString(); }
            catch (Exception) { text = null; }
            var type = item.GetType().FullName;
            return string.IsNullOrEmpty(text) || text == type ? type : type + " " + text;
        }

        /// <summary>Every subscriber held by a game SubscriptionManager or RulebookSubscriptionManager, once per event type.</summary>
        private static List<object> Subscribers(object manager)
        {
            var result = new List<object>();
            var listeners = Field(manager, "m_Listeners") as IEnumerable
                ?? throw new InvalidOperationException($"{manager.GetType().FullName}.m_Listeners is not readable on this game version.");
            foreach (var pair in listeners)
            {
                var list = pair.GetType().GetProperty("Value")?.GetValue(pair, null);
                if (list == null) continue;
                var subscribers = Field(list, "List") as IList
                    ?? throw new InvalidOperationException($"{list.GetType().FullName}.List is not readable on this game version.");
                foreach (var subscriber in subscribers)
                    if (subscriber != null) result.Add(subscriber);
            }
            return result;
        }

        /// <summary>
        /// Every subscriber under every unit in one of the game's per-unit subscription dictionaries, so a subscription
        /// left under a unit that existed before the build is found as well as a new unit's.
        /// </summary>
        private static List<object> UnitSubscriptions(IEnumerable dictionary)
        {
            var result = new List<object>();
            foreach (var pair in dictionary)
            {
                var unit = pair.GetType().GetProperty("Key")?.GetValue(pair, null);
                var manager = pair.GetType().GetProperty("Value")?.GetValue(pair, null);
                if (unit == null || manager == null) continue;
                foreach (var subscriber in Subscribers(manager)) result.Add(new UnitSubscription(unit, subscriber));
            }
            return result;
        }

        /// <summary>One subscriber under one unit's key; equal to another for the same two objects.</summary>
        private sealed class UnitSubscription
        {
            internal UnitSubscription(object unit, object subscriber)
            {
                Unit = unit;
                Subscriber = subscriber;
            }

            internal object Unit { get; }
            internal object Subscriber { get; }

            public override bool Equals(object obj)
                => obj is UnitSubscription other && ReferenceEquals(Unit, other.Unit) && ReferenceEquals(Subscriber, other.Subscriber);

            public override int GetHashCode() => RuntimeHelpers.GetHashCode(Unit) * 31 + RuntimeHelpers.GetHashCode(Subscriber);
        }

        /// <summary>Compares game objects by reference and unit subscriptions by their unit and subscriber.</summary>
        private sealed class EntryComparer : IEqualityComparer<object>
        {
            public static readonly EntryComparer Instance = new();
            public new bool Equals(object x, object y) => x is UnitSubscription subscription ? subscription.Equals(y) : ReferenceEquals(x, y);
            public int GetHashCode(object obj) => obj is UnitSubscription subscription ? subscription.GetHashCode() : RuntimeHelpers.GetHashCode(obj);
        }

        private static object Field(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Instance);
                if (field != null) return field.GetValue(target);
            }
            return null;
        }
    }

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
