using System;
using System.Reflection;
using HarmonyLib;

namespace WotR.Testing.Offline
{
    [HarmonyPatch(typeof(UnityEngine.Random), nameof(UnityEngine.Random.InitState))]
    internal static class UnityRandomInitStatePatch
    {
        private static bool Prefix(int seed)
        {
            Boundaries.Hit("unity-random");
            GameRandom.NoteInitState(seed);
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "RandomRangeInt")]
    internal static class UnityRandomRangeIntPatch
    {
        private static bool Prefix(int minInclusive, int maxExclusive, ref int __result)
        {
            Boundaries.Hit("unity-random");
            __result = GameRandom.RangeInt(minInclusive, maxExclusive);
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), typeof(float), typeof(float))]
    internal static class UnityRandomRangeFloatPatch
    {
        private static bool Prefix(float minInclusive, float maxInclusive, ref float __result)
        {
            Boundaries.Hit("unity-random");
            __result = GameRandom.RangeFloat(minInclusive, maxInclusive);
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "get_value")]
    internal static class UnityRandomValuePatch
    {
        private static bool Prefix(ref float __result)
        {
            Boundaries.Hit("unity-random");
            __result = GameRandom.Value();
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "get_state_Injected")]
    internal static class UnityRandomGetStatePatch
    {
        private static bool Prefix(ref UnityEngine.Random.State ret)
        {
            Boundaries.Hit("unity-random");
            ret = GameRandom.CaptureState();
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "set_state_Injected")]
    internal static class UnityRandomSetStatePatch
    {
        private static bool Prefix(UnityEngine.Random.State value)
        {
            Boundaries.Hit("unity-random");
            GameRandom.ApplyState(value);
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "get_seed")]
    internal static class UnityRandomGetSeedPatch
    {
        private static bool Prefix(ref int __result)
        {
            Boundaries.Hit("unity-random");
            __result = GameRandom.ReadSeed();
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "set_seed")]
    internal static class UnityRandomSetSeedPatch
    {
        private static bool Prefix(int value)
        {
            Boundaries.Hit("unity-random");
            GameRandom.NoteSeedSet(value);
            return false;
        }
    }

    [HarmonyPatch(typeof(Random), MethodType.Constructor, new Type[0])]
    internal static class SystemRandomUnseededPatch
    {
        private static void Prefix()
        {
            Boundaries.Hit("system-random-observe");
            GameRandom.CountUnseeded();
        }
    }

    [HarmonyPatch(typeof(Random), MethodType.Constructor, new[] { typeof(int) })]
    internal static class SystemRandomSeededPatch
    {
        private static void Prefix(int Seed)
        {
            _ = Seed;
            Boundaries.Hit("system-random-observe");
        }
    }
}
