using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Visual effects while a battle runs its game loop. A battle renders nothing, so effects are not spawned: an
    /// ability's AbilitySpawnFx does what it does when its execution context disables effects, and FxHelper's spawn
    /// methods return no handle, which their callers already allow for. Outside battles these calls run as before.
    /// </summary>
    [HarmonyPatch]
    internal static class BattleFxPatches
    {
        private static readonly string[] Spawners =
        {
            "SpawnFx", "SpawnFxOnGameObject", "SpawnFxOnUnit", "SpawnFxOnWeapon", "SpawnFxOnPoint", "SpawnImmediately",
        };

        private static IEnumerable<MethodBase> TargetMethods()
            => typeof(Kingmaker.Visual.Particles.FxHelper).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => Spawners.Contains(method.Name));

        private static bool Prefix(ref object __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("fx");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.Components.Base.AbilitySpawnFx), nameof(Kingmaker.UnitLogic.Abilities.Components.Base.AbilitySpawnFx.Spawn))]
    internal static class AbilitySpawnFxPatch
    {
        private static bool Prefix()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("fx");
            return false;
        }
    }

    /// <summary>
    /// An effect a buff, an ability or another action spawns on a unit or a point (<c>ContextActionSpawnFx</c>): it loads
    /// the effect's prefab from the game's asset bundles, which a battle does not load, and gives it to FxHelper. The game
    /// skips the whole action when the context disables effects; a battle takes the same path.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Mechanics.Actions.ContextActionSpawnFx), nameof(Kingmaker.UnitLogic.Mechanics.Actions.ContextActionSpawnFx.RunAction))]
    internal static class ContextActionSpawnFxPatch
    {
        private static bool Prefix()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("fx");
            return false;
        }
    }

    /// <summary>
    /// The glow of an enchanted weapon a battle unit wields (<c>ItemEnchantment.RespawnFx</c>, when the enchantment
    /// activates, also while the battle builds the unit): it loads the enchantment's prefab from the game's asset bundles,
    /// which a battle does not load, and puts it on the weapon model, which a stand-in does not have. The enchantment's
    /// rules (its context and components) are set up by the game as usual.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Blueprints.Items.Ecnchantments.ItemEnchantment), nameof(Kingmaker.Blueprints.Items.Ecnchantments.ItemEnchantment.RespawnFx))]
    internal static class ItemEnchantmentFxPatch
    {
        private static bool Prefix(Kingmaker.Blueprints.Items.Ecnchantments.ItemEnchantment __instance)
        {
            if (!OfflineBattle.IsBattleUnit(__instance.Owner?.Wielder?.Unit)) return true;
            Boundaries.Hit("fx");
            return false;
        }
    }

    /// <summary>Sound events while a battle runs its game loop: nothing is played, and the game gets no playing id (0).</summary>
    [HarmonyPatch]
    internal static class BattleSoundPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => typeof(Kingmaker.Sound.SoundEventsManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "PostEvent");

        private static bool Prefix(ref uint __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("sound");
            __result = 0;
            return false;
        }
    }

    /// <summary>The action bar's click sound while a battle runs: a slot the battle environment clicks plays it through the game's UI.</summary>
    [HarmonyPatch(typeof(Kingmaker.UI.UnitSettings.MechanicActionBarSlot), nameof(Kingmaker.UI.UnitSettings.MechanicActionBarSlot.PlaySound))]
    internal static class ActionBarSoundPatch
    {
        private static bool Prefix()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("sound");
            return false;
        }
    }
}
