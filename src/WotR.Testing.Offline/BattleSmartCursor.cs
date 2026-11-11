using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Parts;
using TurnBased.Controllers;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The turn-based smart cursor of the game's TurnController: the attack variants it enables for the unit under the
    /// pointer (TurnController.UpdateSmartCursorVariants, after its predictions) and the movement limit of a move on the
    /// ground. Variants are read from the controller and switched only with the player's key (TryChangeSmartAction).
    /// </summary>
    internal static class BattleSmartCursor
    {
        private static readonly FieldInfo Variants = AccessTools.Field(typeof(TurnController), "m_SmartCursorVariants");
        private static readonly FieldInfo CurrentIndex = AccessTools.Field(typeof(TurnController), "m_CurrentSmartCursorIndex");
        private static readonly Type VariantType = AccessTools.Inner(typeof(TurnController), "SmartCursorVariant");
        private static readonly FieldInfo VariantEnabled = AccessTools.Field(VariantType, "Enabled");
        private static readonly FieldInfo VariantMovement = AccessTools.Field(VariantType, "Movement");
        private static readonly FieldInfo VariantAttack = AccessTools.Field(VariantType, "Attack");

        /// <summary>The name of a variant: its movement (a five-foot step or a move) and its attack mode.</summary>
        private static string Name(object variant)
        {
            var movement = (TurnController.MovementLimit)VariantMovement.GetValue(variant) == TurnController.MovementLimit.FiveFootStep ? "five-foot-step" : "move";
            var attack = VariantAttack.GetValue(variant).ToString() switch
            {
                "FullAttack" => "full-attack",
                "SingleAttack" => "single-attack",
                _ => "touch",
            };
            return movement + "+" + attack;
        }

        /// <summary>The variants the game enables now for the unit under the pointer, in its order.</summary>
        internal static IReadOnlyList<string> Enabled(TurnController turn)
            => ((IList)Variants.GetValue(turn)).Cast<object>().Where(v => (bool)VariantEnabled.GetValue(v)).Select(Name).Distinct().ToList();

        /// <summary>The variant the smart cursor is on, or null.</summary>
        internal static string Current(TurnController turn)
        {
            var index = (int)CurrentIndex.GetValue(turn);
            var variants = (IList)Variants.GetValue(turn);
            return index >= 0 && index < variants.Count ? Name(variants[index]) : null;
        }

        /// <summary>
        /// Switches with the player's key (TryChangeSmartAction) until the variant is current. False when the game does not
        /// enable it for the unit under the pointer, or will not switch.
        /// </summary>
        internal static bool Select(TurnController turn, string variant)
        {
            var variants = (IList)Variants.GetValue(turn);
            for (var i = 0; i <= variants.Count; i++)
            {
                if (Current(turn) == variant) return true;
                if (!Enabled(turn).Contains(variant) || !turn.TryChangeSmartAction()) return false;
            }
            return false;
        }

        private static string LimitName(TurnController.MovementLimit limit) => limit switch
        {
            TurnController.MovementLimit.TwoActions => "two-actions",
            TurnController.MovementLimit.OneAction => "one-action",
            _ => "five-foot-step",
        };

        internal static string CurrentLimit(TurnController turn) => LimitName(turn.CurrentMovementLimit);

        /// <summary>Switches a move on the ground to the limit with the player's key, as TryChangeMovementLimit cycles it.</summary>
        internal static bool SelectLimit(TurnController turn, string limit)
        {
            for (var i = 0; i < 3; i++)
            {
                if (CurrentLimit(turn) == limit) return true;
                if (!turn.TryChangeSmartAction()) return false;
            }
            return CurrentLimit(turn) == limit;
        }

        /// <summary>
        /// The movement limits the game's key cycles through for a move on the ground (TryChangeMovementLimit): two actions
        /// always, one action with normal movement left, less than 3 s of move action used and no standard action used, a
        /// five-foot step while the unit has it.
        /// </summary>
        internal static IEnumerable<string> GroundLimits(TurnController turn, UnitEntityData unit)
        {
            yield return "two-actions";
            if (turn.HasNormalMovement(unit) && unit.CombatState.Cooldown.MoveAction < 3f && !unit.UsedStandardAction()) yield return "one-action";
            if (turn.HasFiveFootStep(unit)) yield return "five-foot-step";
        }

        /// <summary>
        /// The attack variants whose conditions on the unit hold, as UpdateSmartCursorVariants checks them: a full attack
        /// with more than one attack and a full-round action (or spell combat), a touch while the unit holds a touch charge,
        /// a five-foot step while the unit has it or the target is within reach, and a move unless the target is within reach
        /// and the unit has its five-foot step. The path the game's preview then finds to the target decides the rest when
        /// the attack is given.
        /// </summary>
        internal static IEnumerable<string> AttackCandidates(TurnController turn, UnitEntityData unit, bool withinReach)
        {
            var attacks = new List<string>();
            if (UnitAttack.EstimateFullAttacks(unit) > 1 && (unit.HasFullRoundAction() || unit.PreparedSpellCombat())) attacks.Add("full-attack");
            attacks.Add("single-attack");
            if (unit.Get<UnitPartTouch>() != null) attacks.Add("touch");
            var movements = new List<string>();
            if (turn.HasFiveFootStep(unit) || withinReach) movements.Add("five-foot-step");
            if (!(withinReach && turn.HasFiveFootStep(unit))) movements.Add("move");
            return movements.SelectMany(m => attacks.Select(a => m + "+" + a)).Where(BattleAction.AttackVariants.Contains);
        }
    }
}
