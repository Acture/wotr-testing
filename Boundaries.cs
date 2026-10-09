using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WotR.OfflineTesting
{
    /// <summary>One declared replacement at the environment edge, with how often the game reached it.</summary>
    public sealed class Boundary
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Replaces { get; set; }
        public string Behavior { get; set; }
        public long Hits { get; set; }

        // Live counter; serialized reports use Hits from Snapshot().
        public long HitsField;
    }

    /// <summary>
    /// Registry of every environment adaptation. Nothing here touches game rules (bonuses, targeting,
    /// buff lifetimes, rulebook calculations); those run as shipped in Assembly-CSharp and the mod.
    /// </summary>
    public static class Boundaries
    {
        private static readonly ConcurrentDictionary<string, Boundary> Registry = new();

        public static void Declare(string id, string category, string replaces, string behavior)
            => Registry[id] = new Boundary { Id = id, Category = category, Replaces = replaces, Behavior = behavior };

        public static void Hit(string id)
        {
            if (Registry.TryGetValue(id, out var boundary)) System.Threading.Interlocked.Increment(ref boundary.HitsField);
        }

        public static IReadOnlyList<Boundary> Snapshot()
            => Registry.Values.OrderBy(b => b.Category).ThenBy(b => b.Id)
                .Select(b => new Boundary { Id = b.Id, Category = b.Category, Replaces = b.Replaces, Behavior = b.Behavior, Hits = b.HitsField })
                .ToList();

        internal static void DeclareHarmonyBoundaries()
        {
            Declare("unity-paths", "environment", "UnityEngine.Application path getters",
                "dataPath/streamingAssetsPath point at the WotR inputs; persistent and cache paths point at artifacts/obj.");
            Declare("unity-platform", "environment", "Application.platform/isEditor/isPlaying, SystemInfo.systemMemorySize, Debug.isDebugBuild",
                "Report a playing Windows release player with 16 GB, as read by startup, settings and quality selection.");
            Declare("unity-log", "logging", "UnityEngine.DebugLogHandler native sink and Application log callback registration",
                "Messages are captured into the test report.");
            Declare("owlcat-log", "logging", "Owlcat.Runtime.Core.Logging.Logger.Log",
                "Messages and exceptions are captured into the test report with their severity; nothing is suppressed from it.");
            Declare("shader-ids", "graphics", "UnityEngine.Shader.PropertyToID",
                "Stable integer per property name, used only by visual static constructors.");
            Declare("scene-objects", "scene", "UnityEngine.Object.FindObjectsOfType and Resources.FindObjectsOfTypeAll",
                "Returns an empty array: no scene is loaded.");
            Declare("bug-report-service", "telemetry", "Kingmaker.Utility.ReportingUtils constructor",
                "Skipped; it would otherwise contact the developer's report server.");
            Declare("graphics-settings", "graphics", "GraphicsSettingsController constructor and GraphicsPresetsController.AutodetectQuality",
                "Skipped; they need scene coroutine objects and GPU detection.");
            Declare("referenced-assets", "assets", "BlueprintReferencedAssets.Get(int) during blueprint deserialization",
                "Entries are read from the real blueprint.assets list; ScriptableObjects are mapped from their type tree as the field type the game expects, native assets stay null.");
        }
    }

    internal static class HarnessState
    {
        public static OfflineInputs Inputs;
        public static string PersistentDirectory;
        public static UnityAssetMapper Assets;
        public static readonly ConcurrentQueue<CapturedLog> Logs = new();
    }

    public sealed class CapturedLog
    {
        public string Source { get; set; }
        public string Severity { get; set; }
        public string Channel { get; set; }
        public string Message { get; set; }
        public string Exception { get; set; }
    }

    [HarmonyPatch(typeof(UnityEngine.Application))]
    internal static class ApplicationPatches
    {
        [HarmonyPatch(nameof(UnityEngine.Application.dataPath), MethodType.Getter), HarmonyPrefix]
        private static bool DataPath(ref string __result) => Path(ref __result, System.IO.Path.Combine(HarnessState.Inputs.InputRoot, "Wrath_Data"));

        [HarmonyPatch(nameof(UnityEngine.Application.streamingAssetsPath), MethodType.Getter), HarmonyPrefix]
        private static bool StreamingAssetsPath(ref string __result) => Path(ref __result, HarnessState.Inputs.StreamingAssets);

        [HarmonyPatch(nameof(UnityEngine.Application.persistentDataPath), MethodType.Getter), HarmonyPrefix]
        private static bool PersistentDataPath(ref string __result) => Path(ref __result, HarnessState.PersistentDirectory);

        [HarmonyPatch(nameof(UnityEngine.Application.temporaryCachePath), MethodType.Getter), HarmonyPrefix]
        private static bool TemporaryCachePath(ref string __result) => Path(ref __result, System.IO.Path.Combine(HarnessState.PersistentDirectory, "cache"));

        [HarmonyPatch(nameof(UnityEngine.Application.platform), MethodType.Getter), HarmonyPrefix]
        private static bool Platform(ref UnityEngine.RuntimePlatform __result)
        {
            Boundaries.Hit("unity-platform");
            __result = UnityEngine.RuntimePlatform.WindowsPlayer;
            return false;
        }

        [HarmonyPatch(nameof(UnityEngine.Application.isEditor), MethodType.Getter), HarmonyPrefix]
        private static bool IsEditor(ref bool __result)
        {
            Boundaries.Hit("unity-platform");
            __result = false;
            return false;
        }

        [HarmonyPatch(nameof(UnityEngine.Application.isPlaying), MethodType.Getter), HarmonyPrefix]
        private static bool IsPlaying(ref bool __result)
        {
            Boundaries.Hit("unity-platform");
            __result = true;
            return false;
        }

        [HarmonyPatch("SetLogCallbackDefined"), HarmonyPrefix]
        private static bool SetLogCallbackDefined()
        {
            Boundaries.Hit("unity-log");
            return false;
        }

        private static bool Path(ref string result, string path)
        {
            Boundaries.Hit("unity-paths");
            Directory.CreateDirectory(path);
            result = path;
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.SystemInfo), nameof(UnityEngine.SystemInfo.systemMemorySize), MethodType.Getter)]
    internal static class SystemMemoryPatch
    {
        private static bool Prefix(ref int __result)
        {
            Boundaries.Hit("unity-platform");
            __result = 16384;
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.isDebugBuild), MethodType.Getter)]
    internal static class DebugBuildPatch
    {
        private static bool Prefix(ref bool __result)
        {
            Boundaries.Hit("unity-platform");
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class UnityLogPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var handler = AccessTools.TypeByName("UnityEngine.DebugLogHandler");
            yield return AccessTools.Method(handler, "Internal_Log");
            yield return AccessTools.Method(handler, "Internal_LogException");
        }

        private static bool Prefix(object[] __args)
        {
            Boundaries.Hit("unity-log");
            HarnessState.Logs.Enqueue(new CapturedLog
            {
                Source = "unity",
                Severity = __args[0] is Exception ? "Exception" : __args[0]?.ToString(),
                Message = __args.Length > 2 ? __args[2]?.ToString() : null,
                Exception = (__args[0] as Exception)?.ToString(),
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(Owlcat.Runtime.Core.Logging.Logger), nameof(Owlcat.Runtime.Core.Logging.Logger.Log))]
    internal static class OwlcatLogPatch
    {
        private static bool Prefix(Owlcat.Runtime.Core.Logging.LogChannel channel, Owlcat.Runtime.Core.Logging.LogSeverity severity, Exception ex, object message, object[] par)
        {
            Boundaries.Hit("owlcat-log");
            string text;
            try
            {
                text = par is { Length: > 0 } && message is string format ? string.Format(format, par) : message?.ToString();
            }
            catch (FormatException)
            {
                text = message?.ToString();
            }
            HarnessState.Logs.Enqueue(new CapturedLog { Source = "owlcat", Severity = severity.ToString(), Channel = channel?.Name, Message = text, Exception = ex?.ToString() });
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Shader), nameof(UnityEngine.Shader.PropertyToID))]
    internal static class ShaderPropertyIdPatch
    {
        private static readonly Dictionary<string, int> Ids = new();

        private static bool Prefix(string name, ref int __result)
        {
            Boundaries.Hit("shader-ids");
            lock (Ids)
            {
                if (!Ids.TryGetValue(name, out __result)) Ids[name] = __result = Ids.Count + 1;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.FindObjectsOfType), typeof(Type), typeof(bool))]
    internal static class FindObjectsOfTypePatch
    {
        private static bool Prefix(Type type, ref UnityEngine.Object[] __result)
        {
            Boundaries.Hit("scene-objects");
            __result = (UnityEngine.Object[])Array.CreateInstance(type, 0);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class FindObjectsOfTypeAllPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("UnityEngine.ResourcesAPIInternal"), "FindObjectsOfTypeAll");

        private static bool Prefix(Type type, ref UnityEngine.Object[] __result)
        {
            Boundaries.Hit("scene-objects");
            __result = (UnityEngine.Object[])Array.CreateInstance(type, 0);
            return false;
        }
    }

    [HarmonyPatch(typeof(Kingmaker.Utility.ReportingUtils), MethodType.Constructor)]
    internal static class ReportingUtilsPatch
    {
        private static bool Prefix()
        {
            Boundaries.Hit("bug-report-service");
            return false;
        }
    }

    [HarmonyPatch(typeof(Kingmaker.Settings.Graphics.GraphicsSettingsController), MethodType.Constructor)]
    internal static class GraphicsSettingsControllerPatch
    {
        private static bool Prefix()
        {
            Boundaries.Hit("graphics-settings");
            return false;
        }
    }

    [HarmonyPatch(typeof(Kingmaker.Settings.Graphics.GraphicsPresetsController), "AutodetectQuality")]
    internal static class GraphicsAutodetectPatch
    {
        private static bool Prefix()
        {
            Boundaries.Hit("graphics-settings");
            return false;
        }
    }

    /// <summary>Tracks the field type the game's blueprint deserializer is reading, so referenced assets map to that type.</summary>
    [HarmonyPatch(typeof(Kingmaker.Blueprints.JsonSystem.BinaryFormat.ReflectionBasedSerializer), "ReadField")]
    internal static class ReadFieldPatch
    {
        [ThreadStatic] private static Stack<Type> expected;

        public static Type Current
        {
            get
            {
                if (expected == null || expected.Count == 0) return null;
                var type = expected.Peek();
                if (type.IsArray) return type.GetElementType();
                if (type.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(type)) return type.GetGenericArguments()[0];
                return type;
            }
        }

        private static void Prefix(FieldInfo field) => (expected ??= new Stack<Type>()).Push(field.FieldType);

        private static void Finalizer() => expected?.Pop();
    }

    [HarmonyPatch(typeof(Kingmaker.SharedTypes.BlueprintReferencedAssets), nameof(Kingmaker.SharedTypes.BlueprintReferencedAssets.Get), typeof(int))]
    internal static class ReferencedAssetsPatch
    {
        private static readonly ConcurrentDictionary<(int, Type), UnityEngine.Object> Cache = new();

        private static bool Prefix(int index, ref UnityEngine.Object __result)
        {
            Boundaries.Hit("referenced-assets");
            var type = ReadFieldPatch.Current;
            __result = Cache.GetOrAdd((index, type), key => HarnessState.Assets.MapReferencedAsset(key.Item1, key.Item2));
            return false;
        }
    }
}
