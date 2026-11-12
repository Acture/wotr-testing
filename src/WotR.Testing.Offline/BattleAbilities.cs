using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.UI.UnitSettings;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.ActivatableAbilities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Utility;

namespace WotR.Testing.Offline
{
    /// <summary>A spell, ability, usable item or toggle the unit's action bar offers, with the game's own slot for it.</summary>
    internal sealed class BattleAbilitySlot
    {
        internal BattleAbilitySlot(string id, MechanicActionBarSlot slot, AbilityData ability, ActivatableAbility toggle)
        {
            Id = id;
            Slot = slot;
            Ability = ability;
            Toggle = toggle;
        }

        /// <summary>
        /// spell:BOOK:LEVEL:SPELL[+METAMAGIC], item:QUICKSLOT:ITEM, ability:ABILITY or toggle:TOGGLE (blueprint names); a
        /// conversion the slot offers (a spontaneous conversion, an ability variant) appends &gt;CONVERTED. A repeated id gets
        /// #2, #3 and so on, in the action bar's order.
        /// </summary>
        internal string Id { get; }

        internal MechanicActionBarSlot Slot { get; }

        /// <summary>What the slot casts or uses; null for a toggle.</summary>
        internal AbilityData Ability { get; }

        /// <summary>The ability the slot switches on or off; null when it casts.</summary>
        internal ActivatableAbility Toggle { get; }
    }

    /// <summary>The game's action bar for a battle unit: what it offers, whether it can be used, and the command it gives.</summary>
    internal static class BattleAbilities
    {
        private static readonly MethodInfo IsNotAvailable = AccessTools.PropertyGetter(typeof(MechanicActionBarSlot), "IsNotAvailable");

        /// <summary>
        /// Everything the game's action bar offers the unit, collected as ActionBarVM does (CollectSpells, CollectItems,
        /// CollectAbilities): the spells of its spellbooks (ActionBarSpellbookHelper.Fetch), the usable items in its quick
        /// slots, its abilities that are not hidden, cantrips or temporarily disabled, and its toggles that are not hidden or
        /// temporarily disabled; after each slot, the conversions it offers (MechanicActionBarSlot.GetConvertedAbilityData).
        /// </summary>
        internal static List<BattleAbilitySlot> Collect(UnitEntityData unit)
        {
            var slots = new List<BattleAbilitySlot>();
            var used = new Dictionary<string, int>();
            string Unique(string id)
            {
                used.TryGetValue(id, out var count);
                used[id] = ++count;
                return count == 1 ? id : id + "#" + count;
            }
            void Add(string id, MechanicActionBarSlot slot)
            {
                id = Unique(id);
                var (ability, toggle) = Content(slot);
                slots.Add(new BattleAbilitySlot(id, slot, ability, toggle));
                foreach (var converted in slot.GetConvertedAbilityData().GetMechanicSlots(unit))
                {
                    var (convertedAbility, convertedToggle) = Content(converted);
                    var name = convertedAbility?.Blueprint.name ?? convertedToggle?.Blueprint.name;
                    if (name != null) slots.Add(new BattleAbilitySlot(Unique(id + ">" + name), converted, convertedAbility, convertedToggle));
                }
            }

            foreach (var spell in ActionBarSpellbookHelper.Fetch(unit))
                Add($"spell:{spell.Spell.Spellbook?.Blueprint.name}:{spell.Spell.SpellLevel}:{spell.Spell.Blueprint.name}{Metamagic(spell.Spell)}", spell);
            var quickSlots = unit.Body.QuickSlots;
            for (var i = 0; i < quickSlots.Length; i++)
                if (quickSlots[i].HasItem && quickSlots[i].Item is Kingmaker.Items.ItemEntityUsable item)
                    Add($"item:{i}:{item.Blueprint.name}", new MechanicActionBarSlotItem { Item = item, Unit = unit });
            foreach (var ability in unit.Abilities)
                if (!ability.Hidden && !ability.Blueprint.IsCantrip && !ability.Data.TemporarilyDisabled)
                    Add("ability:" + ability.Blueprint.name, new MechanicActionBarSlotAbility { Ability = ability.Data, Unit = unit });
            foreach (var toggle in unit.ActivatableAbilities)
                if (!toggle.Blueprint.HiddenInUI && !toggle.TemporarilyDisabled)
                    Add("toggle:" + toggle.Blueprint.name, new MechanicActionBarSlotActivableAbility { ActivatableAbility = toggle, Unit = unit });
            return slots;
        }

        /// <summary>
        /// The uses left of the usable items in the unit's quick slots, by their action bar id (item:QUICKSLOT:ITEM), as the
        /// action bar shows them (MechanicActionBarSlotItem.GetResource): a single item's charges (a wand), a stack's count
        /// (potions, scrolls), -1 for an item that spends nothing.
        /// </summary>
        internal static SortedDictionary<string, int> ItemUses(UnitEntityData unit)
        {
            var uses = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
            var quickSlots = unit.Body.QuickSlots;
            for (var i = 0; i < quickSlots.Length; i++)
                if (quickSlots[i].HasItem && quickSlots[i].Item is Kingmaker.Items.ItemEntityUsable item)
                    uses[$"item:{i}:{item.Blueprint.name}"] = new MechanicActionBarSlotItem { Item = item, Unit = unit }.GetResource();
            return uses;
        }

        private static string Metamagic(AbilityData spell)
            => spell.MetamagicData is { MetamagicMask: var mask } && mask != 0 ? "+" + mask.ToString().Replace(", ", "+") : "";

        private static (AbilityData Ability, ActivatableAbility Toggle) Content(MechanicActionBarSlot slot) => slot switch
        {
            MechanicActionBarSlotSpell spell => (spell.Spell, null),
            MechanicActionBarSlotSpontaneusConvertedSpell converted => (converted.Spell, null),
            MechanicActionBarSlotAbility ability => (ability.Ability, null),
            MechanicActionBarSlotActivableAbility toggle => (null, toggle.ActivatableAbility),
            MechanicActionBarSlotItem item when item.Item.Ability != null => (item.Item.Ability.Data, null),
            MechanicActionBarSlotItem item => (null, item.Item.ActivatableAbility),
            _ => (null, null),
        };

        /// <summary>
        /// Whether the player can use the slot now, as the action bar decides (IsPossibleActive, which in turn-based combat
        /// includes the current turn's actions); a toggle that is on can also be switched off when the slot allows it.
        /// </summary>
        internal static bool PlayerCanUse(BattleAbilitySlot slot)
            => slot.Slot.IsPossibleActive() || (slot.Slot is MechanicActionBarSlotActivableAbility toggle && toggle.IsPossibleDeactivate());

        /// <summary>
        /// For a unit the player does not control (a controlled enemy in self-play): the slot's own availability and
        /// resources, without the player's turn checks, which ask whether the player may end the turn. Whether the unit has
        /// the action left is the command's turn-based start rule, as for the game's AI.
        /// </summary>
        internal static bool AvailableToUnit(BattleAbilitySlot slot)
            => slot.Slot.Unit.Descriptor.State.IsConscious
                && (((bool)IsNotAvailable.Invoke(slot.Slot, null) == false && !slot.Slot.IsDisabled(slot.Slot.GetResource()))
                    || (slot.Slot is MechanicActionBarSlotActivableAbility toggle && toggle.IsPossibleDeactivate()));

        /// <summary>
        /// The target a click on the unit gives the ability, as ClickWithSelectedAbilityHandler.GetTarget does: the caster
        /// for abilities without target selection, the unit, or for point abilities the unit's position facing away from the
        /// caster (a bomb targets the unit).
        /// </summary>
        internal static TargetWrapper TargetOn(AbilityData ability, UnitEntityData target)
        {
            switch (ability.TargetAnchor)
            {
                case AbilityTargetAnchor.Owner:
                    return ability.Caster.Unit;
                case AbilityTargetAnchor.Unit:
                    return new TargetWrapper(target);
                default:
                    if (ability.Blueprint.GetComponent<AbilityIsBomb>() != null) return new TargetWrapper(target);
                    var direction = target.Position - ability.Caster.Unit.Position;
                    var facing = direction != UnityEngine.Vector3.zero ? UnityEngine.Quaternion.LookRotation(direction) : UnityEngine.Quaternion.identity;
                    return new TargetWrapper(target.Position, facing.eulerAngles.y, target);
            }
        }

        /// <summary>
        /// The target a click on the ground gives a point ability, as ClickWithSelectedAbilityHandler.GetTarget does: the
        /// point, facing away from the caster.
        /// </summary>
        internal static TargetWrapper TargetAt(AbilityData ability, UnityEngine.Vector3 point)
        {
            var direction = point - ability.Caster.Unit.Position;
            var facing = direction != UnityEngine.Vector3.zero ? UnityEngine.Quaternion.LookRotation(direction) : UnityEngine.Quaternion.identity;
            return new TargetWrapper(point, facing.eulerAngles.y, null);
        }

        /// <summary>
        /// The command a click with the selected ability gives, as ClickWithSelectedAbilityHandler.CreateCommand chooses it:
        /// a custom move or attack, an item's use, or the cast. The handler's UI events (cast and item use requested) are not
        /// raised; the command is the same.
        /// </summary>
        internal static UnitCommand CommandFor(AbilityData ability, TargetWrapper target)
        {
            if (ability.Blueprint.GetComponent<AbilityCustomMove>() != null) return new UnitMoveTo(target.Point);
            if (ability.Blueprint.GetComponent<AbilityCustomAttack>() != null) return UnitAttack.CreateAttackCommand(ability.Caster.Unit, target.Unit);
            if (ability.SourceItem != null) return new UnitUseAbility(ability, target);
            return UnitUseAbility.CreateCastCommand(ability, target);
        }
    }
}
