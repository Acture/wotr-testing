using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace WotR.Testing.Offline
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
            Declare("umm-log", "logging", "UnityModManager.Logger.Write, WriteBuffers and Clear",
                "Every manager and mod log line goes to the report (logs/<source>.log); Log.txt in the installation is never written or deleted.");
            Declare("shader-ids", "graphics", "UnityEngine.Shader.PropertyToID",
                "Stable integer per property name, used only by visual static constructors.");
            Declare("scene-objects", "scene", "UnityEngine.Object.FindObjectsOfType and Resources.FindObjectsOfTypeAll",
                "Returns an empty array: no scene is loaded.");
            Declare("bug-report-service", "telemetry", "Kingmaker.Utility.ReportingUtils constructor",
                "Skipped; it would otherwise contact the developer's report server.");
            Declare("graphics-settings", "graphics", "GraphicsSettingsController constructor and GraphicsPresetsController.AutodetectQuality",
                "Skipped; they need scene coroutine objects and GPU detection.");
            Declare("runtime-images", "graphics", "Texture2D constructors, ImageConversion.LoadImage and Sprite.Create",
                "Images a mod builds at run time (for example icons from PNG files) have no native texture: constructors are skipped, LoadImage returns false and Sprite.Create returns null, as native assets from bundles are null.");
            Declare("referenced-assets", "assets", "BlueprintReferencedAssets.Get(int) during blueprint deserialization",
                "Entries are read from the real blueprint.assets list; ScriptableObjects are mapped from their type tree as the field type the game expects, native assets stay null.");
            Declare("unity-random", "environment", "UnityEngine.Random native entries",
                "One System.Random, seed 1 at startup. Integer Range is [min, max). Float Range and value are [min, max). This is not the Unity player's sequence. Random.state values are valid only in this process. Negative seeds follow System.Random's absolute value.");
            Declare("system-random-observe", "environment", "System.Random constructors",
                "Counts constructions and leaves the game's own System.Random instances unchanged. Unseeded constructions are reported and are not given the game seed.");
            Declare("profiling-counters", "telemetry", "Owlcat.Runtime.Core.ProfilingCounters.Counter.AddMeasurement",
                "Skipped: it files a timing under the current Unity frame (Time.frameCount), and no frame exists offline. Only profiler statistics are affected; the measured game code runs as shipped.");
            Declare("unit-view-stand-in", "scene", "UnitEntityView of battle units and the components on its GameObject",
                "Battle units get a view with no GameObject, model or animator, which Unity's checks read as live, so the game's controllers run the unit. The prefab is the one UnitEntityData.CreateView picks (an active polymorph's first), read on demand from its .unit bundle (BattleScenario.CorpulenceOverrideBySize replaces only its corpulence; without the bundle an override or CorpulenceFallbackBySize gives the corpulence and everything else keeps class defaults): like Instantiate, the stand-in copies the serialized fields of the prefab's UnitEntityView (corpulence, rotation lock), OverrideSizeScale and UnitMovementAgent (acceleration, speeds, avoidance); references to models and colliders stay null and are listed in the report. The game's AttachView and DetachView attach and detach it, with the view's side (EntityViewBase.AttachToData, DetachFromData) replaced by what the mechanics read: the game's own UnitMovementAgent with the SeekerWithClearance, RaycastModifier and FunnelModifiers its Init creates, the size scale, and the entity-bounds sphere on its own transform, which the game's EntityBoundsController moves. The components' transform reads as the unit (its position and orientation); writes to its position or rotation are dropped, because only a model would show them. Enabled flags come from the prefab and are kept without a native object; enabling or disabling the movement agent runs its own OnEnable or OnDisable. The stand-ins are distinct objects under Unity's ==; GetInstanceID needs native code and fails as unity-runtime-required. Polymorph.TryReplaceView and RestoreView swap stand-ins: the polymorph's prefab with size scaling off, then the unit's own; the view replacement record, interrupting all commands but movement and waking the unit run as in the game, models and the visual fade are skipped. Removed with DetachView before the unit is destroyed.");
            Declare("unit-weapon-visuals", "graphics", "UnitEntityView.HandleAttackCommandRun and UnitHandEquipmentController.DidUpdate for battle units",
                "Skipped: drawing weapon models in the unit's hands, toggling occlusion clipping, and waiting for the hand models to match the combat state. The stand-in has no models, so the wait ends at once.");
            Declare("unit-hit-effects", "graphics", "HitPlayer.PlayDamageHit when the target or attacker has a battle view stand-in",
                "Skipped: blood, hit effects and sounds of dealt damage. The game takes the same early return when either unit has no live view; damage itself is dealt by RuleDealDamage as shipped.");
            Declare("unity-math", "environment", "UnityEngine Quaternion, Matrix4x4 and Vector3 native math",
                "Managed versions with Unity's conventions (left-handed basis, Euler order Z, X, Y): look rotation, Euler conversion, inverse, angle-axis, from-to, lerp and slerp, matrix inverse, determinant, transpose, identity check and TRS, vector rotate-towards and slerp. Used for example when a unit turns or the game's A* code builds a graph transform. Results can differ from Unity's native code in the last bits of a float.");
            Declare("hand-equipment-controller", "environment", "Game.HandsEquipmentController during a battle with an area",
                "The default game mode provides UnitHandEquipmentController; offline no mode runs, so a battle with an area sets a new instance of the game's controller for its game loop and restores the previous value at cleanup.");
            Declare("navigation-graph", "assets", "AstarPath.active and the area's navigation graph",
                "Read as the game's NavMeshManager does: the area part's GraphCache asset, located through Bundles/locationlist.json in its .nav bundle, deserialized by the game's A* code into an AstarPath component without a GameObject. Path requests run on A*'s own non-threaded path processor; Unity's Update of the component runs once per battle frame.");
            Declare("physics-scene", "scene", "UnityEngine.PhysicsScene native queries while a battle runs its game loop",
                "Only during a battle with an area; outside battles these native calls fail as unity-runtime-required, as before. No level is loaded, so no level colliders exist: casts hit nothing and overlaps find nothing. The game's EntityBoundsController creates its entity-bounds scene (a scene handle with no level, valid only during the battle) and keeps each unit's bound sphere at the unit's position with the unit's corpulence as radius; overlaps on the unit bounds layer are answered from those spheres, the colliders the game's entity-bounds physics scene holds. Vision range and area queries use them. Heights follow the navigation mesh instead of ground colliders.");
            Declare("debug-draw", "graphics", "UnityEngine.Debug.DrawLine",
                "Skipped: editor scene-view lines, which a player build does not draw either.");
            Declare("move-target-marker", "ui", "UnitEntityView.OnMovementStarted for battle units",
                "Skipped: when a directly controllable unit starts a path, the method only places the destination marker of the UI's ClickPointerManager, which does not exist offline. The movement itself is UnitMovementAgent's and runs as shipped.");
            Declare("frame-clock", "environment", "TimeController frame duration and game time during battle frames; Time.frameCount and Time.realtimeSinceStartup while a battle runs its game loop",
                "Each battle frame is 1/30 s. It starts as a Unity frame does: the frame count and real time advance before A* and the controllers run. In TimeController's place, DeltaTime and GameDeltaTime are min(PlayerTimeScale * DebugTimeScale / 12, Time.deltaTime) and game time grows by it, as TimeController.Tick does in the default game mode outside turn-based combat; Time.deltaTime is the 1/30 s frame scaled by PlayerTimeScale * DebugTimeScale * UiTimeScale, unscaled in the first frame after the battle's loop starts. Between battle frames every read of the frame count is a new frame, so per-frame caches never return a value from before a change; outside battles Time.frameCount and Time.realtimeSinceStartup fail as unity-runtime-required, as before.");
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
            // Unity returns these paths with '/' separators on Windows, and game code splits them on '/'.
            result = path.Replace('\\', '/');
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

    // Explicit parameters instead of __args, which Harmony before 2.1 cannot inject (-Harmony game is 2.0.4).
    [HarmonyPatch]
    internal static class UnityLogPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("UnityEngine.DebugLogHandler"), "Internal_Log");

        private static bool Prefix(UnityEngine.LogType level, string msg)
        {
            Boundaries.Hit("unity-log");
            HarnessState.Logs.Enqueue(new CapturedLog { Source = "unity", Severity = level.ToString(), Message = msg });
            return false;
        }
    }

    [HarmonyPatch]
    internal static class UnityLogExceptionPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("UnityEngine.DebugLogHandler"), "Internal_LogException");

        private static bool Prefix(Exception ex)
        {
            Boundaries.Hit("unity-log");
            HarnessState.Logs.Enqueue(new CapturedLog { Source = "unity", Severity = "Exception", Exception = ex?.ToString() });
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

    /// <summary>Owlcat's profiler measures code such as the entity position-changed event; it keys timings by Unity frame.</summary>
    [HarmonyPatch(typeof(Owlcat.Runtime.Core.ProfilingCounters.Counter), nameof(Owlcat.Runtime.Core.ProfilingCounters.Counter.AddMeasurement))]
    internal static class ProfilingCounterPatch
    {
        private static bool Prefix()
        {
            Boundaries.Hit("profiling-counters");
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

    [HarmonyPatch]
    internal static class Texture2DConstructorPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredConstructors(typeof(UnityEngine.Texture2D)).Where(c => !c.IsStatic);

        private static bool Prefix()
        {
            Boundaries.Hit("runtime-images");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class LoadImagePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => AccessTools.GetDeclaredMethods(Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", throwOnError: true)).Where(m => m.Name == "LoadImage");

        private static bool Prefix(ref bool __result)
        {
            Boundaries.Hit("runtime-images");
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class SpriteCreatePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
            => AccessTools.GetDeclaredMethods(typeof(UnityEngine.Sprite)).Where(m => m.Name == nameof(UnityEngine.Sprite.Create) && m.IsPublic);

        private static bool Prefix(ref UnityEngine.Sprite __result)
        {
            Boundaries.Hit("runtime-images");
            __result = null;
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
