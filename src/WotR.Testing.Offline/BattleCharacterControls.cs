using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Armies.TacticalCombat;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.GameModes;
using Kingmaker.Items;
using Kingmaker.PubSubSystem;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Class.Kineticist;
using TurnBased.Controllers;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// What a character exposes to the player besides its commands, as the game's own buttons and keys set it: the
    /// stealth, hold and stop buttons of a party unit, the right click that chooses its auto-use ability, the weapon sets
    /// of the action bar and the grip of the current set. The AI button is not among them: the decision maker takes the
    /// place of the game's AI for its units.
    /// </summary>
    internal static class BattleCharacterControls
    {
        private static readonly MethodInfo SetStealthEnabled = AccessTools.Method(typeof(ControlCharactersVM), "SetStealthEnabled");

        /// <summary>
        /// The switches the game offers the unit now. A unit the player can select (<c>IsDirectlyControllable</c>) has the
        /// hold, stop and stealth buttons and keys at any time, and a right click on every action bar entry the game accepts
        /// for auto use (or that is the unit's auto-use ability). Every controlled unit that can take a command has its weapon
        /// sets, when the game lets it change them, and the grip of its current set, when the set allows the other grip.
        /// </summary>
        internal static IEnumerable<BattleAction> Candidates(string id, UnitEntityData unit, IReadOnlyList<BattleAbilitySlot> slots, bool commandable, bool turnBased)
        {
            var player = unit.IsDirectlyControllable;
            if (player)
            {
                yield return BattleAction.Hold(id);
                yield return BattleAction.Stop(id);
                yield return BattleAction.Stealth(id);
                foreach (var slot in slots)
                    if (BattleAbilities.AutoUseData(slot) is { } data && (unit.Brain?.AutoUseAbility == data || data.IsSuitableForAutoUse))
                        yield return BattleAction.AutoUse(id, slot.Id);
            }
            if (!unit.Descriptor.State.IsConscious || (!player && !commandable)) yield break;
            if (CanToggleGrip(unit)) yield return BattleAction.Grip(id);
            if (CanChangeWeaponSet(unit, player, turnBased))
                for (var set = 0; set < unit.Body.HandsEquipmentSets.Count; set++)
                    if (set != unit.Body.CurrentHandEquipmentSetIndex) yield return BattleAction.WeaponSet(id, set);
        }

        /// <summary>
        /// Whether the weapon set bar takes a click: in real time always; in turn-based combat on the turn's unit while it
        /// is not prone, may end its turn and has its move or standard action (ActionBarWeaponVM.UpdateCanChange). A unit the
        /// player does not control follows the game's own rule (UnitHelper.CanChangeWeaponSet). Polymorphs that replace
        /// the hands forbid the change (UnitBody.IsEquipmentSetChangeForbidden).
        /// </summary>
        private static bool CanChangeWeaponSet(UnitEntityData unit, bool player, bool turnBased)
        {
            if (unit.Body.IsEquipmentSetChangeForbidden) return false;
            if (!player) return unit.CanChangeWeaponSet();
            if (!turnBased) return true;
            var turn = Game.Instance.TurnBasedCombatController.CurrentTurn;
            return turn != null && turn.SelectedUnit == unit && !unit.Descriptor.State.Prone.Active && turn.CanEndTurnAndNoActing()
                && (unit.HasMoveAction() || unit.HasStandardAction());
        }

        /// <summary>
        /// Whether the current weapon set's grip button switches (WeaponSetGripVM.Refresh): the action bar's set is the
        /// current one, it is not empty and it allows the other grip.
        /// </summary>
        private static bool CanToggleGrip(UnitEntityData unit)
        {
            var body = unit.Body;
            var set = body.HandsEquipmentSets[body.CurrentHandEquipmentSetIndex];
            return set == body.CurrentHandsEquipmentSet && !set.IsEmpty() && set.IsGripAllowed(OtherGrip(set));
        }

        private static GripType OtherGrip(HandsEquipmentSet set) => set.GripType == GripType.TwoHanded ? GripType.OneHanded : GripType.TwoHanded;

        /// <summary>Sets the switch as the game's button or key sets it for the unit alone.</summary>
        internal static void Issue(UnitEntityData unit, BattleAction action, IReadOnlyList<BattleAbilitySlot> slots)
        {
            switch (action.Kind)
            {
                case BattleActionKind.Hold:
                    // SelectionManagerBase.SwitchHold for the selected unit.
                    if (!unit.HoldState) unit.Hold();
                    else unit.CancelHold();
                    return;
                case BattleActionKind.Stop:
                    // SelectionManagerBase.SwitchStop for the selected unit.
                    if (!unit.StopState) unit.Stop();
                    else unit.CancelStop();
                    return;
                case BattleActionKind.Stealth:
                    // ControlCharactersVM.OnStealthClick for the selected unit: the game's own SetStealthEnabled, which asks
                    // the stealth controller at once whether the unit enters stealth.
                    SetStealthEnabled.Invoke(null, new object[] { unit, !unit.Descriptor.State.IsInStealth });
                    return;
                case BattleActionKind.AutoUse:
                    slots.First(s => s.Id == action.Ability).Slot.OnRightClick();
                    return;
                case BattleActionKind.WeaponSet:
                    // ActionBarWeaponSetVM.OnSetCurrentSet.
                    if (!unit.Body.IsEquipmentSetChangeForbidden)
                        unit.Body.CurrentHandEquipmentSetIndex = int.Parse(action.Variant, CultureInfo.InvariantCulture);
                    return;
                case BattleActionKind.Grip:
                {
                    // WeaponSetGripVM.TryToggleGrip for the current set: the other grip, when the button switches.
                    var body = unit.Body;
                    var set = body.HandsEquipmentSets[body.CurrentHandEquipmentSetIndex];
                    if (CanToggleGrip(unit)) set.TryChangeGrip(OtherGrip(set));
                    return;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.Kind, null);
            }
        }
    }

    /// <summary>
    /// The view's side of a weapon set change for battle units (boundary <c>weapon-set-change</c>). In the game,
    /// UnitEntityView.HandleUnitChangeActiveEquipmentSet hands the change to its UnitViewHandsEquipment. In combat, while the
    /// unit can act, StartCombatChangeAnimation marks the running standard command to stop as soon as possible and starts
    /// the coroutine AnimateEquipmentChangeInCombat (a change while one animates goes along with it). The coroutine holds
    /// the hands busy, so commands wait (UnitCommandController.ShouldStartCommand); from the next frame it waits while a
    /// real-time unit lacks both its move and its standard action; then, unless a kinetic blade is drawn or put away, it
    /// spends the move action, or the standard action when the move action is used, and tells the turn
    /// (TurnController.ApplyUnitChangeActiveEquipmentSet); then it animates the weapons. Out of combat, or when the unit
    /// cannot act, the weapons change at once. Battle units have view stand-ins without hands, so this does the same for
    /// them, without the animation: the hands are free again when the action is spent.
    /// </summary>
    internal sealed class BattleWeaponSets : IUnitActiveEquipmentSetHandler
    {
        private sealed class Change
        {
            internal Change(UnitEntityData unit, bool kineticBlade, int frame)
            {
                Unit = unit;
                KineticBlade = kineticBlade;
                Frame = frame;
            }

            internal UnitEntityData Unit { get; }
            internal bool KineticBlade { get; }
            internal int Frame { get; }
        }

        private static readonly List<Change> Changes = new();
        private static BattleWeaponSets instance;
        private static int frame;

        /// <summary>The weapon set before the change being announced, captured by the setter.</summary>
        internal static HandsEquipmentSet Previous { get; set; }

        internal static void Install()
        {
            Changes.Clear();
            frame = 0;
            instance = new BattleWeaponSets();
            EventBus.Subscribe(instance);
        }

        internal static void Remove()
        {
            if (instance != null) EventBus.Unsubscribe(instance);
            instance = null;
            Changes.Clear();
            Previous = null;
        }

        /// <summary>Whether a change of the unit's weapons still holds its hands, as the coroutine does.</summary>
        internal static bool IsChanging(UnitEntityData unit)
        {
            foreach (var change in Changes)
                if (ReferenceEquals(change.Unit, unit)) return true;
            return false;
        }

        public void HandleUnitChangeActiveEquipmentSet(UnitDescriptor descriptor)
        {
            var previous = Previous;
            Previous = null;
            var unit = descriptor.Unit;
            if (!OfflineBattle.IsBattleUnit(unit)) return;
            Boundaries.Hit("weapon-set-change");
            if (!unit.IsInCombat || !unit.Descriptor.State.CanAct || TacticalCombatHelper.IsActive || IsChanging(unit)) return;
            if (unit.Commands.Standard != null) unit.Commands.Standard.InterruptAsSoonAsPossible = true;
            Changes.Add(new Change(unit, KineticBlade(unit.Body, previous), frame));
        }

        /// <summary>
        /// The coroutine's check of the weapons in the hand that changes: the main hand when its item changed, else the off
        /// hand, before and after.
        /// </summary>
        private static bool KineticBlade(UnitBody body, HandsEquipmentSet previous)
        {
            previous ??= body.CurrentHandsEquipmentSet;
            var mainChanged = previous.PrimaryHand.MaybeItem != body.PrimaryHand.MaybeItem;
            var now = mainChanged ? body.PrimaryHand.MaybeItem : body.SecondaryHand.MaybeItem;
            var before = mainChanged ? previous.PrimaryHand.MaybeItem : previous.SecondaryHand.MaybeItem;
            return now?.Blueprint?.GetComponent<WeaponKineticBlade>() != null || before?.Blueprint?.GetComponent<WeaponKineticBlade>() != null;
        }

        /// <summary>Counts a battle frame; a change started in it resumes in a later one, as a coroutine does.</summary>
        internal static void BeginFrame() => frame++;

        /// <summary>The coroutines' part of a frame, after the game's tick, where Unity resumes coroutines.</summary>
        internal static void Update()
        {
            for (var i = 0; i < Changes.Count; i++)
            {
                var change = Changes[i];
                var unit = change.Unit;
                if (unit.Destroyed || unit.IsDisposed)
                {
                    Changes.RemoveAt(i--);
                    continue;
                }
                if (change.Frame >= frame || Waits(unit)) continue;
                if (!change.KineticBlade)
                {
                    var cooldown = unit.CombatState.Cooldown;
                    if (Math.Abs(cooldown.MoveAction) < 0.01f) cooldown.MoveAction = 3f;
                    else cooldown.StandardAction = 3f;
                    Game.Instance.TurnBasedCombatController.CurrentTurn?.ApplyUnitChangeActiveEquipmentSet(unit.Descriptor);
                }
                Changes.RemoveAt(i--);
            }
        }

        private static bool Waits(UnitEntityData unit)
        {
            var turnBased = CombatController.IsInTurnBasedCombat();
            var cooldown = unit.CombatState.Cooldown;
            return (!TacticalCombatHelper.IsActive && !turnBased && unit.CombatState.IsInCombat
                    && (!unit.CombatState.CanActInCombat || (Math.Abs(cooldown.MoveAction) > 0.01f && Math.Abs(cooldown.StandardAction) > 0.01f)))
                || (turnBased && Game.Instance.CurrentMode == GameModeType.FullScreenUi);
        }
    }

    /// <summary>Captures the weapon set a battle unit changes from, for the view's check of the hands.</summary>
    [HarmonyPatch(typeof(UnitBody), nameof(UnitBody.CurrentHandEquipmentSetIndex), MethodType.Setter)]
    internal static class WeaponSetChangePatch
    {
        private static void Prefix(UnitBody __instance)
        {
            if (BattleLoop.Running) BattleWeaponSets.Previous = __instance.CurrentHandsEquipmentSet;
        }
    }

    /// <summary>A battle unit's hands are busy while its weapon set change waits, as the view's coroutine holds them.</summary>
    [HarmonyPatch(typeof(UnitEntityData), nameof(UnitEntityData.AreHandsBusyWithAnimation), MethodType.Getter)]
    internal static class WeaponSetHandsPatch
    {
        private static void Postfix(UnitEntityData __instance, ref bool __result)
        {
            if (!__result && BattleLoop.Running && BattleWeaponSets.IsChanging(__instance)) __result = true;
        }
    }
}
