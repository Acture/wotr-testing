using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Stats;
using Kingmaker.Enums;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// A battle described by its consumer: units on each side from real blueprints, what to change on them through the
    /// game's own APIs, and paths the consumer or the game already has. <see cref="OfflineBattle"/> builds a battle from
    /// it and can rebuild the same initial state; the scenario itself holds no game objects.
    /// </summary>
    public sealed class BattleScenario
    {
        public BattleScenario(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A scenario name is required.", nameof(name));
            Name = name;
        }

        public string Name { get; }

        public IList<BattleUnitSpec> Units { get; } = new List<BattleUnitSpec>();

        public IList<BattlePathSpec> Paths { get; } = new List<BattlePathSpec>();

        /// <summary>
        /// Sides whose units act only on the commands given to them: the game's AI never decides for them, not even
        /// the actions it takes for party members whose AI the player switched off (boundary <c>external-control</c>).
        /// The game's AI decides for every other unit in combat, as in the game, where the party's own AI follows
        /// <c>UnitEntityData.IsAIEnabled</c>. Commands are the game's own, as its AI or the player would give them.
        /// </summary>
        public ISet<string> ControlledSides { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Id of a player-faction unit that becomes the player's main character through the game's
        /// <c>Player.SetMainCharacter</c> for the battle. Rules that consult the player character need one, for example
        /// <c>RuleDealDamage</c> through <c>GameHelper.IsAttackingGreenNPC</c> when a party unit damages a non-party unit.
        /// Without it such a rule logs the game's own NullReferenceException and the operation fails.
        /// </summary>
        public string MainCharacter { get; set; }

        /// <summary>
        /// Ids of player-faction units that join the player's party next to the <see cref="MainCharacter"/> with the game's
        /// <c>Player.AddCompanion</c>, and leave it with <c>Player.RemoveCompanion</c> at cleanup. The game lets the player
        /// command only party members (<c>UnitEntityData.IsDirectlyControllable</c>): on a controlled side they get their
        /// commands as the player gives them, through the action bar and the pointer. The game's turn-based surprise round
        /// also counts only party members as the party. Other player-faction units fight as the player's allies, and on a
        /// controlled side they get commands as the game's AI would give them.
        /// </summary>
        public ISet<string> Companions { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Game time (<c>Player.GameTime</c>) at which the battle starts; cleanup restores the time from before the build.
        /// Without it the battle starts at the session's game time, which is zero. The time matters to the game's
        /// turn-based surprise round: <c>CombatController.HandleCombatStart</c> counts a party member as attacking by
        /// surprise when the seconds part (<c>TimeSpan.Seconds</c>, 0 to 59) of the time since its last offensive action
        /// out of combat is below 6. A unit that never made one counts from game time zero, so in a fight that starts in
        /// the first six seconds of a game minute every such party member surprises the enemies, whoever saw whom first.
        /// </summary>
        public TimeSpan? StartTime { get; set; }

        /// <summary>
        /// Guid of the <c>BlueprintArea</c> the battle loads as the game's current area, with the game's own
        /// <c>AreaPersistentState</c>. Units that are not in the party are kept in its main state. Game frames need it:
        /// unit speed and vision read the loaded area's settings. No scene is loaded.
        /// </summary>
        public string Area { get; set; }

        /// <summary>
        /// Turns on the game's turn-based mode for the battle with the game's own setting
        /// <c>SettingsRoot.Game.TurnBased.EnableTurnBasedMode</c>, as a player switches it, and restores the previous value
        /// at cleanup. The game's <c>TurnBased.Controllers.CombatController</c> then runs combat in turns: it orders the
        /// units by initiative, starts rounds and lets one unit act at a time, while the rest of the game waits. As in the
        /// game, combat becomes turn-based when the party enters it, so a turn-based battle needs an
        /// <see cref="Area"/> (whose navigation graphs include the grid the game's turns use) and a
        /// <see cref="MainCharacter"/>.
        /// </summary>
        public bool TurnBased { get; set; }

        /// <summary>
        /// The game's difficulty level for the battle, chosen as a player chooses it in the game's options: the game's
        /// <c>DifficultyPresetsController</c> fills in that level's preset and the values are confirmed. The rules read
        /// them: the damage enemies deal to the party, the enemies' stat adjustments and critical hits, the AI actions
        /// kept for higher difficulties and its additional behaviours, death's door and more. They favor the enemies of
        /// the player's party; <c>Unfair</c> is the hardest. Cleanup restores the previous settings. Unset keeps the
        /// session's settings, which start as the game's default preset.
        /// </summary>
        public Kingmaker.Settings.GameDifficultyOption? Difficulty { get; set; }

        /// <summary>
        /// The game's "limited AI" setting for the battle (<c>SettingsRoot.Difficulty.LimitedAI</c>), set as a player sets
        /// it and restored at cleanup. When on, the game's AI acting for a directly controllable party unit uses only the
        /// actions marked for limited AI; it does not change the enemies' AI. Unset keeps the session's setting.
        /// </summary>
        public bool? LimitedAI { get; set; }

        /// <summary>
        /// Corpulence in meters that replaces the view prefab's, by a unit's original size (for a polymorph's view, the
        /// unit's size at that moment). A unit's corpulence is the radius reach, engagement, attacks of opportunity, spacing
        /// and vision use; it comes from the view prefab the battle reads on demand from the unit's <c>.unit</c> bundle. An
        /// override changes only the corpulence: the prefab is still read when the inputs have its bundle, and its movement
        /// agent and size override apply; the game still scales the value when the unit's size changes. Without the bundle
        /// the view and movement agent keep their class defaults. An override gives the same corpulence with and without
        /// the bundles.
        /// </summary>
        public IDictionary<Size, float> CorpulenceOverrideBySize { get; } = new Dictionary<Size, float>();

        /// <summary>
        /// Corpulence in meters, by a unit's original size, used only when the inputs lack the unit's <c>.unit</c> bundle,
        /// as a snapshot does; with the bundle the prefab's own corpulence applies. The view and movement agent then keep
        /// their class defaults. Results can differ between the installation and a snapshot. A unit whose bundle is missing
        /// and whose size has neither an override nor a fallback fails the build as environment-missing.
        /// </summary>
        public IDictionary<Size, float> CorpulenceFallbackBySize { get; } = new Dictionary<Size, float>();

        /// <summary>
        /// Errors the game logs while this battle is built that the scenario explains. Any other logged error fails the
        /// build. Each allowance needs a reason.
        /// </summary>
        public IList<RuleLogAllowance> SetupLogAllowances { get; } = new List<RuleLogAllowance>();

        /// <summary>
        /// Errors the game logs while battle frames run that the scenario explains, each with a reason; the library already
        /// allows the game's own known logs. Any other logged error fails the frames.
        /// </summary>
        public IList<RuleLogAllowance> FrameLogAllowances { get; } = new List<RuleLogAllowance>();

        public BattleUnitSpec Unit(string id, string side, string blueprint)
        {
            var unit = new BattleUnitSpec(id, side, blueprint);
            Units.Add(unit);
            return unit;
        }

        internal void Validate()
        {
            if (Units.Count == 0) throw new ArgumentException($"Scenario {Name} has no units.");
            var duplicate = Units.GroupBy(u => u.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new ArgumentException($"Scenario {Name} declares unit {duplicate.Key} {duplicate.Count()} times.");
            var paths = Paths.GroupBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (paths != null) throw new ArgumentException($"Scenario {Name} declares path {paths.Key} {paths.Count()} times.");
            var ids = new HashSet<string>(Units.Select(u => u.Id), StringComparer.Ordinal);
            if (MainCharacter != null && !ids.Contains(MainCharacter))
                throw new ArgumentException($"Main character {MainCharacter} is not a unit of scenario {Name}.");
            foreach (var companion in Companions)
            {
                if (!ids.Contains(companion)) throw new ArgumentException($"Companion {companion} is not a unit of scenario {Name}.");
                if (MainCharacter == null) throw new ArgumentException($"Scenario {Name} has companions without a MainCharacter; the party forms around the main character.");
                if (companion == MainCharacter) throw new ArgumentException($"Main character {MainCharacter} of scenario {Name} is already in the party; it is not also a companion.");
            }
            if (StartTime < TimeSpan.Zero) throw new ArgumentException($"Scenario {Name} starts at game time {StartTime}; game time is not negative.");
            if (Difficulty == Kingmaker.Settings.GameDifficultyOption.Custom)
                throw new ArgumentException($"Scenario {Name} asks for the Custom difficulty; choose a level, as Custom is what the game shows after individual settings change.");
            if (TurnBased && Area == null)
                throw new ArgumentException($"Scenario {Name} is turn-based without an Area; the game's turns run in game frames on the area's navigation grid.");
            if (TurnBased && MainCharacter == null)
                throw new ArgumentException($"Scenario {Name} is turn-based without a MainCharacter; the game runs turns only while the party is in combat.");
            foreach (var side in ControlledSides)
                if (!Units.Any(u => u.Side == side))
                    throw new ArgumentException($"Controlled side {side} has no unit in scenario {Name}.");
            foreach (var path in Paths)
            {
                path.Validate();
                foreach (var end in new[] { path.From, path.To })
                    if (end != null && !ids.Contains(end))
                        throw new ArgumentException($"Path {path.Id} names unit {end}, which scenario {Name} does not declare.");
            }
            foreach (var (allowance, kind) in SetupLogAllowances.Select(a => (a, "setup")).Concat(FrameLogAllowances.Select(a => (a, "frame"))))
            {
                if (allowance == null) throw new ArgumentException($"Scenario {Name} has a null {kind} log allowance.");
                if (string.IsNullOrWhiteSpace(allowance.Reason)) throw new ArgumentException($"Scenario {Name} has a {kind} log allowance without a reason.");
                if (allowance.Match == null) throw new ArgumentException($"Scenario {Name} has a {kind} log allowance without a match.");
            }
        }
    }

    /// <summary>
    /// One unit built from a vanilla or mod <c>BlueprintUnit</c>. The blueprint decides everything not listed here. Listed
    /// changes are applied through the game's APIs by kind, in this order: faction, class levels, base stats, equipment,
    /// facts, abilities, buffs, resources, position; changes of one kind in the order they were added. Each one is
    /// checked afterwards.
    /// </summary>
    public sealed class BattleUnitSpec
    {
        public BattleUnitSpec(string id, string side, string blueprint)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A unit id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(side)) throw new ArgumentException("A side is required.", nameof(side));
            if (string.IsNullOrWhiteSpace(blueprint)) throw new ArgumentException("A unit blueprint guid is required.", nameof(blueprint));
            Id = id;
            Side = side;
            Blueprint = blueprint;
        }

        /// <summary>Scenario-local name, stable across resets.</summary>
        public string Id { get; }

        /// <summary>The consumer's grouping, for example "party" or "enemies". Hostility comes from factions, not sides.</summary>
        public string Side { get; }

        public string Blueprint { get; }

        /// <summary><c>BlueprintFaction</c> guid applied with <c>UnitDescriptor.SwitchFactions</c> after the unit is built.</summary>
        public string Faction { get; set; }

        /// <summary>Class levels added with the game's <c>AddClassLevels.LevelUp</c>, which also makes its default selections.</summary>
        public IList<BattleClassLevels> ClassLevels { get; } = new List<BattleClassLevels>();

        /// <summary>Base values set on <c>ModifiableValue.BaseValue</c>; modifiers from facts and items still apply.</summary>
        public IDictionary<StatType, int> BaseStats { get; } = new Dictionary<StatType, int>();

        /// <summary>Items inserted with <c>ItemSlot.InsertItem</c>; a slot that refuses the item fails the build.</summary>
        public IList<BattleEquipment> Equipment { get; } = new List<BattleEquipment>();

        /// <summary><c>BlueprintUnitFact</c> guids (features and other facts) added with <c>AddFact</c>.</summary>
        public IList<string> Facts { get; } = new List<string>();

        /// <summary><c>BlueprintAbility</c> guids added with <c>AddFact</c>.</summary>
        public IList<string> Abilities { get; } = new List<string>();

        public IList<BattleBuff> Buffs { get; } = new List<BattleBuff>();

        /// <summary>
        /// Current amount for <c>BlueprintAbilityResource</c> guids the unit already has, set with the game's
        /// <c>Restore</c> and <c>Spend</c>. A resource the unit does not have fails the build.
        /// </summary>
        public IDictionary<string, int> Resources { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Position set on <c>UnitEntityData.Position</c>; no scene, navmesh or collision checks it.</summary>
        public BattlePoint? Position { get; set; }

        public BattleUnitSpec WithFaction(string faction)
        {
            Faction = faction;
            return this;
        }

        public BattleUnitSpec WithClassLevels(string characterClass, int levels)
        {
            ClassLevels.Add(new BattleClassLevels(characterClass, levels));
            return this;
        }

        public BattleUnitSpec WithBaseStat(StatType stat, int value)
        {
            BaseStats[stat] = value;
            return this;
        }

        public BattleUnitSpec Equip(BattleSlot slot, string item)
        {
            Equipment.Add(new BattleEquipment(slot, item));
            return this;
        }

        public BattleUnitSpec WithFact(string fact)
        {
            Facts.Add(fact);
            return this;
        }

        public BattleUnitSpec WithAbility(string ability)
        {
            Abilities.Add(ability);
            return this;
        }

        public BattleUnitSpec WithBuff(string buff, int? rounds = null)
        {
            Buffs.Add(new BattleBuff(buff, rounds));
            return this;
        }

        public BattleUnitSpec WithResource(string resource, int amount)
        {
            Resources[resource] = amount;
            return this;
        }

        public BattleUnitSpec At(float x, float y, float z)
        {
            Position = new BattlePoint(x, y, z);
            return this;
        }
    }

    public sealed class BattleClassLevels
    {
        public BattleClassLevels(string characterClass, int levels)
        {
            if (string.IsNullOrWhiteSpace(characterClass)) throw new ArgumentException("A character class guid is required.", nameof(characterClass));
            if (levels < 1) throw new ArgumentOutOfRangeException(nameof(levels), levels, "Add at least one level.");
            CharacterClass = characterClass;
            Levels = levels;
        }

        public string CharacterClass { get; }
        public int Levels { get; }
    }

    public enum BattleSlot
    {
        PrimaryHand,
        SecondaryHand,
        Armor,
        Shirt,
        Belt,
        Head,
        Glasses,
        Feet,
        Gloves,
        Neck,
        Ring1,
        Ring2,
        Wrist,
        Shoulders,

        /// <summary>The unit body's quick slots for usable items (potions, scrolls, wands), in the action bar's order.</summary>
        QuickSlot1,
        QuickSlot2,
        QuickSlot3,
        QuickSlot4,
        QuickSlot5,
    }

    public sealed class BattleEquipment
    {
        public BattleEquipment(BattleSlot slot, string item)
        {
            if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("An item blueprint guid is required.", nameof(item));
            Slot = slot;
            Item = item;
        }

        public BattleSlot Slot { get; }
        public string Item { get; }
    }

    public sealed class BattleBuff
    {
        /// <param name="buff"><c>BlueprintBuff</c> guid, applied with the unit itself as caster.</param>
        /// <param name="rounds">Duration in rounds (6 seconds each); null for a buff without a duration.</param>
        public BattleBuff(string buff, int? rounds = null)
        {
            if (string.IsNullOrWhiteSpace(buff)) throw new ArgumentException("A buff blueprint guid is required.", nameof(buff));
            if (rounds is < 1) throw new ArgumentOutOfRangeException(nameof(rounds), rounds, "A duration is at least one round.");
            Buff = buff;
            Rounds = rounds;
        }

        public string Buff { get; }
        public int? Rounds { get; }
    }

    public readonly struct BattlePoint : IEquatable<BattlePoint>
    {
        public BattlePoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public bool Equals(BattlePoint other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is BattlePoint other && Equals(other);
        public override int GetHashCode() => (X, Y, Z).GetHashCode();
        public override string ToString() => FormattableString.Invariant($"({X:R}, {Y:R}, {Z:R})");
    }

    /// <summary>
    /// A path that already exists: recorded from the game, produced by the game's pathfinding elsewhere, or supplied by
    /// the consumer. The battle keeps it as given, with its source; it never computes, repairs or measures a path.
    /// </summary>
    public sealed class BattlePathSpec
    {
        public BattlePathSpec(string id, string source, IEnumerable<BattlePoint> points)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A path id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Name where the path came from.", nameof(source));
            Id = id;
            Source = source;
            Points = (points ?? throw new ArgumentNullException(nameof(points))).ToList().AsReadOnly();
        }

        /// <summary>Copies the waypoints of a path the game already computed (for example <c>Pathfinding.Path.vectorPath</c>).</summary>
        public static BattlePathSpec FromGamePoints(string id, string source, IEnumerable<UnityEngine.Vector3> points)
            => new(id, source, (points ?? throw new ArgumentNullException(nameof(points))).Select(p => new BattlePoint(p.x, p.y, p.z)));

        public string Id { get; }

        /// <summary>Where the path came from, for example "consumer:recorded-in-game" or "game:Pathfinding.ABPath".</summary>
        public string Source { get; }

        /// <summary>Waypoints including both endpoints, in order.</summary>
        public IReadOnlyList<BattlePoint> Points { get; }

        /// <summary>Unit id at the start of the path, if the path belongs to a unit.</summary>
        public string From { get; set; }

        /// <summary>Unit id at the end of the path, if the path leads to a unit.</summary>
        public string To { get; set; }

        /// <summary>Length as supplied with the path; null when the source gave none.</summary>
        public double? Distance { get; set; }

        /// <summary>Movement cost as supplied with the path; null when the source gave none.</summary>
        public double? Cost { get; set; }

        /// <summary>Unit of <see cref="Distance"/> and <see cref="Cost"/> as given by the source, for example "meters" or "feet".</summary>
        public string Units { get; set; }

        internal void Validate()
        {
            if (Points.Count < 2) throw new ArgumentException($"Path {Id} needs at least its two endpoints.");
            if (Distance is < 0) throw new ArgumentException($"Path {Id} has a negative distance.");
            if (Cost is < 0) throw new ArgumentException($"Path {Id} has a negative cost.");
        }
    }
}
