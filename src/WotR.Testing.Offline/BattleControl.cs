using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Units of a battle's controlled sides (<see cref="BattleScenario.ControlledSides"/>): the game's AI does not decide
    /// for them, so they act only on the commands their decision maker gives. Every other unit keeps the game's AI.
    /// </summary>
    internal static class BattleControl
    {
        private static readonly HashSet<object> Controlled = new(ReferenceComparer.Instance);

        internal static void Set(IEnumerable<UnitEntityData> units)
        {
            Controlled.Clear();
            foreach (var unit in units) Controlled.Add(unit);
        }

        internal static void Clear() => Controlled.Clear();

        internal static bool IsControlled(UnitEntityData unit) => unit is not null && Controlled.Contains(unit);

        internal static int Count => Controlled.Count;
    }

    /// <summary>
    /// AiBrainController.TickBrain, which chooses a unit's next action in real time and on its turn in turn-based combat,
    /// does nothing for a controlled unit.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.AI.AiBrainController), "TickBrain")]
    internal static class ControlledBrainPatch
    {
        private static bool Prefix(UnitEntityData unit)
        {
            if (!BattleControl.IsControlled(unit)) return true;
            Boundaries.Hit("external-control");
            return false;
        }
    }
}
