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
}
