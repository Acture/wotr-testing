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
                "Entries are read from the real blueprint.assets list; ScriptableObjects are mapped from their type tree as the field type the game expects, native assets stay null. Mapped ScriptableObjects read as alive under Unity's checks (if (asset), asset != null), as loaded assets do; fields of Unity's own structs that Unity serializes under another name (Bounds.m_Extent, LayerMask.m_Bits) go to their managed fields.");
            Declare("asset-names", "assets", "UnityEngine.Object.name of ScriptableObjects mapped from blueprint.assets",
                "Returns the asset's serialized m_Name, which Unity keeps in native memory. The game reads it, for example, as the key of a keyboard binding; any other object's name fails as unity-runtime-required.");
            Declare("unity-random", "environment", "UnityEngine.Random native entries",
                "One System.Random, seed 1 at startup. Integer Range is [min, max). Float Range and value are [min, max). This is not the Unity player's sequence. Random.state values are valid only in this process. Negative seeds follow System.Random's absolute value.");
            Declare("system-random-observe", "environment", "System.Random constructors",
                "Counts constructions and leaves the game's own System.Random instances unchanged. Unseeded constructions are reported and are not given the game seed.");
            Declare("profiling-counters", "telemetry", "Owlcat.Runtime.Core.ProfilingCounters.Counter.AddMeasurement",
                "Skipped: it files a timing under the current Unity frame (Time.frameCount), and no frame exists offline. Only profiler statistics are affected; the measured game code runs as shipped.");
            Declare("unit-view-stand-in", "scene", "UnitEntityView of battle units and the components on its GameObject",
                "Battle units get a view with no GameObject, model or animator, which Unity's checks read as live, so the game's controllers run the unit. The prefab is the one UnitEntityData.CreateView picks (an active polymorph's first), read on demand from its .unit bundle (BattleScenario.CorpulenceOverrideBySize replaces only its corpulence; without the bundle an override or CorpulenceFallbackBySize gives the corpulence and everything else keeps class defaults): like Instantiate, the stand-in copies the serialized fields of the prefab's UnitEntityView (corpulence, rotation lock), OverrideSizeScale and UnitMovementAgent (acceleration, speeds, avoidance); references to models and colliders stay null and are listed in the report. The game's AttachView and DetachView attach and detach it, with the view's side (EntityViewBase.AttachToData, DetachFromData) replaced by what the mechanics read: the game's own UnitMovementAgent with the SeekerWithClearance, RaycastModifier and FunnelModifiers its Init creates, the size scale, and the entity-bounds sphere on its own transform, which the game's EntityBoundsController moves. The components' transform reads as the unit (its position and orientation); writes to its position or rotation are dropped, because only a model would show them. Enabled flags come from the prefab and are kept without a native object; enabling or disabling the movement agent runs its own OnEnable or OnDisable. The stand-in GameObjects keep an active flag: deactivating one runs OnDisable of its enabled agent, activating it OnEnable, as Unity does, and physics overlaps skip disabled bound spheres. The stand-ins are distinct objects under Unity's ==; GetInstanceID needs native code and fails as unity-runtime-required. Polymorph.TryReplaceView and RestoreView swap stand-ins: the polymorph's prefab with size scaling off, then the unit's own; the view replacement record, interrupting all commands but movement and waking the unit run as in the game, models and the visual fade are skipped. Removed with DetachView before the unit is destroyed.");
            Declare("unit-weapon-visuals", "graphics", "UnitEntityView.HandleAttackCommandRun, HideOffWeapon and UnitHandEquipmentController.DidUpdate for battle units",
                "Skipped: drawing weapon models in the unit's hands, toggling occlusion clipping, and waiting for the hand models to match the combat state. The stand-in has no models, so the wait ends at once.");
            Declare("unit-hit-effects", "graphics", "HitPlayer.PlayDamageHit when the target or attacker has a battle view stand-in, and HitPlayer.PlayProjectileHit while a battle runs",
                "Skipped: blood, hit effects and sounds of dealt damage, and the hit, miss and decal effects of a projectile that reaches its target (prefabs from asset bundles a battle does not load). The game takes the same early return for damage when either unit has no live view; damage itself is dealt by RuleDealDamage, and a projectile's rules run in Projectile.OnHit after the effects, as shipped.");
            Declare("occlusion-clipping", "graphics", "UnitEntityView.HandleOccludedObjectDepthClipper for battle units",
                "Skipped: the component that cuts away scenery hiding a party member from the camera, which the game's Player.AddCompanion adds to the unit's GameObject and RemoveCompanion switches off. The stand-in has no GameObject and nothing is drawn; joining and leaving the party run as shipped.");
            Declare("unit-highlight", "ui", "UnitEntityView.UpdateHighlight for battle units",
                "Skipped: the highlight of a unit under the mouse, in a drag box or among the objects the player can use. The stand-in has no highlighter and no UI exists offline; the visibility the game sets before it is kept.");
            Declare("camera", "graphics", "Game.CameraController, TurnController.TryScrollToUnit and IslesController.TickCameraRotation while a battle runs",
                "No camera exists in a battle. Game.CameraController is the default mode's own CameraController without its Activate or Tick, which bind the follow key and move the camera rig: the game's code that follows or releases a unit with the camera (for example when a turn starts) sets the follower's state, and nothing scrolls. When a turn starts, the camera does not scroll to the unit (TryScrollToUnit returns false), so the turn is prepared at once instead of after the camera's scroll, as when the player switches the scroll off. Reading the camera rig's rotation to announce which way the camera faces for the Isles is skipped; the isles themselves tick as shipped.");
            Declare("ability-cast-fx", "graphics", "UnitUseAbility.CastAbilityFx when a battle unit uses an ability",
                "Skipped: the ability blueprint's AbilitySpawnFx effects, the precast effects in the hands and on the ground, and the caster's spell voice. The ability is cast and resolved by the game's code.");
            Declare("fx", "graphics", "FxHelper spawn methods, AbilitySpawnFx.Spawn and ContextActionSpawnFx.RunAction while a battle runs",
                "Skipped: visual effects. A battle renders nothing; ability effects and effects that actions spawn (for example a rage buff's) are skipped as when their execution context disables effects, and FxHelper returns no handle, which its callers allow for. The rules that spawn them run as shipped.");
            Declare("sound", "audio", "SoundEventsManager.PostEvent and the action bar's click sound (MechanicActionBarSlot.PlaySound) while a battle runs",
                "Skipped: sound events; the game gets no playing id (0). The code that posts them runs as shipped. An action bar slot the battle environment clicks plays no click sound through the game's UI.");
            Declare("external-control", "control", "AiBrainController.TickBrain for units of a battle's controlled sides (BattleScenario.ControlledSides)",
                "Skipped for those units only: the game's AI does not choose their actions in real time or on their turns, not even the actions it takes for party members whose AI is switched off. They act on the commands their decision maker gives, with the command classes the game's AI and player use. Rules that force actions, such as confusion or fear, still apply. Units of other sides keep the game's AI. This is a difference from the game, made on request; reports count it.");
            Declare("unit-death-visuals", "graphics", "UnitEntityView.HandleDeath for battle units",
                "Skipped: the blood and dismemberment effect, the fade, occlusion clipping, a swarm's health display, the prefab's death effects and moving the click collider with the torso. The stand-in has no model; the death itself (life state, leaving combat, interrupted commands, experience, death events) is UnitLifeController's and runs as shipped.");
            Declare("unit-get-up-visuals", "graphics", "UnitEntityView.LeaveProneState for battle units",
                "Skipped: the ragdoll's return to its animation, the weapon models in the hands and moving the click collider. The rest of the method runs as the game's: the get-up time (for half a second IsGetUp counts the unit as prone for its AC and holds back its commands), IsProne and the unit's obstacle groups. Standing up itself (when, and the prone state) is UnitProneController's and runs as shipped.");
            Declare("unity-math", "environment", "UnityEngine Quaternion, Matrix4x4, Vector3 and Bounds native math",
                "Managed versions with Unity's conventions (left-handed basis, Euler order Z, X, Y): look rotation, Euler conversion, inverse, angle-axis, from-to, lerp and slerp, matrix inverse, determinant, transpose, identity check and TRS, vector rotate-towards and slerp, and whether bounds contain a point (a point on a face is inside). Used for example when a unit turns or the game's A* code builds a graph transform. Results can differ from Unity's native code in the last bits of a float.");
            Declare("loading-process", "scene", "LoadingProcess.Instance while a battle runs its game loop",
                "A LoadingProcess component without a GameObject for the battle's duration, instead of the one the game creates on a new GameObject: a battle loads no scenes and shows no loading screens. The game's code asks it as usual, for example the turn-based CombatController resets the manual loading screen when combat starts; its Update and coroutines never run.");
            Declare("main-thread-dispatcher", "scene", "UniRx MainThreadDispatcher while a battle runs its game loop",
                "A MainThreadDispatcher without a GameObject for the battle's duration, instead of the one UniRx creates on a new GameObject. Actions the game posts to the main thread (for example the turn-based CombatController when combat ends) run in the dispatcher's own Update at the end of every battle frame and once when the battle stops, as Unity's next frame would; its coroutines are not started.");
            Declare("pointer", "input", "Game.ClickEventsController and the PointerController's pointed object, position and clicks while a battle runs",
                "The game's DefaultPointerController is the default mode's click controller, as Game.SetupPublicControllers sets it, but it does not tick: there is no mouse, camera or level to cast rays into. The battle environment points it at a unit's view or at a point of the ground (which has no object, as no level is loaded) and clicks as PointerController.Tick handles a left click: the game picks the click handler by its priorities, the current turn may ignore the click in turn-based combat, and the handler's OnClick runs. The hover change is announced to the game as the pointer announces it. After a step's clicks the pointer moves off the units, as the player moves the mouse away, so until the next decision the game's predictions for the unit under the pointer do not run on a target that died there (a loot prediction on a corpse reads the view's click collider, which a stand-in lacks). At cleanup, before the units go, the pointer moves off the unit it is on and the player's selection is cleared as an unloading area clears it (SelectionCharacterController.OnAreaBeginUnloading); the reset compares both.");
            Declare("bark", "ui", "Game.UI.BarkManager while a battle runs",
                "A bark player that shows nothing and returns no handle, instead of the one the UI's overtips register: a battle loads no UI. The game's UnitLifeController calls it directly for the \"crippled\" notice when a player-faction unit at death's door comes to; code that goes through UIAccess.Bark already skips a missing player.");
            Declare("selection-ui", "graphics", "SelectionManagerBase.Instance (Game.UI.SelectionManager) while a battle runs",
                "A SelectionManagerPC without a GameObject, registered as its Awake registers it, for the game's click handlers that ask which selected unit acts. The selection itself is the game's Game.SelectionCharacter state, which the turn-based TurnController sets to the unit whose turn it is. Its selection marks are never created and it does not follow party changes, which it handles with UI coroutines.");
            Declare("path-preview", "graphics", "PathVisualizer while a battle runs",
                "The turn-based path preview without a GameObject: its line renderer and decals are stand-ins without native objects, its Update runs every battle frame, and the game's predictions ask it for the path of the command under the pointer, which its movement simulation shortens to the movement left. Only drawing the line (DecimateLine, SetGradientsToRenderer: points, colors, materials, decal positions) is skipped.");
            Declare("cursor", "graphics", "CursorController.SetCursor while a battle runs",
                "Skipped: setting the mouse cursor's image. The game's CursorController still decides which cursor it would show.");
            Declare("input", "input", "UnityEngine.Input keys, buttons, axes and mouse position while a battle runs",
                "No keyboard or mouse: no key or button is held, pressed or released, axes are at rest and the mouse is at the origin, so the game takes its branches for no modifier key held (for example no Ctrl to force a move).");
            Declare("game-mode", "environment", "The game's default mode during a battle with an area: Game.DoStartMode, Game.Tick and Game.DoStopMode",
                "The default mode GameModesFactory builds, without the presentation controllers BattleLoop.Excluded lists (input, pause, camera, fog of war, effects, animation, decals, weather, UI windows, QA, leaderboard, save import, and placing units on level colliders). It is pushed on the game's mode stack, started and activated, and stopped and popped at cleanup, as Game.DoStartMode and DoStopMode do; of the public controllers only those the mechanics use (hand equipment, combat engagement) are set, not the pointer, interaction highlight or camera. Each frame runs Game.Tick's scheduled actions, real time, mode tick and temporary entities, but not its sound, atlas and statistics updates. A request to switch to another mode fails the frame.");
            Declare("navigation-graph", "assets", "AstarPath.active and the area's navigation graph",
                "Read as the game's NavMeshManager does: the area part's GraphCache asset, located through Bundles/locationlist.json in its .nav bundle, deserialized by the game's A* code into an AstarPath component without a GameObject, with the serialized settings of the AstarPath in the area's mechanics scene (its .scenes bundle, read on demand, or a snapshot's navigation-settings.json; class defaults and a report warning without either). Path requests run on A*'s own non-threaded path processor; Unity's Update of the component runs once per battle frame.");
            Declare("physics-scene", "scene", "UnityEngine.PhysicsScene native queries and the Physics.defaultPhysicsScene getter while a battle runs its game loop",
                "Only during a battle with an area; outside battles these native calls fail as unity-runtime-required, as before. The default physics scene is a PhysicsScene with no handle. No level is loaded, so no level colliders exist: casts hit nothing and overlaps find nothing. The game's EntityBoundsController creates its entity-bounds scene (a scene handle with no level, valid only during the battle) and keeps each unit's bound sphere at the unit's position with the unit's corpulence as radius; overlaps on the unit bounds layer are answered from those spheres, the colliders the game's entity-bounds physics scene holds. Vision range and area queries use them. Heights follow the navigation mesh instead of ground colliders.");
            Declare("debug-draw", "graphics", "UnityEngine.Debug.DrawLine",
                "Skipped: editor scene-view lines, which a player build does not draw either.");
            Declare("move-target-marker", "ui", "UnitEntityView.OnMovementStarted for battle units",
                "Skipped: when a directly controllable unit starts a path, the method only places the destination marker of the UI's ClickPointerManager, which does not exist offline. The movement itself is UnitMovementAgent's and runs as shipped.");
            Declare("projectile-view", "graphics", "ProjectileController.CreateView and GameObjectsPool.Release of projectile views while a battle runs",
                "A projectile (an arrow, a ray, a fireball, a cone) gets a stand-in view object that keeps the position and rotation its flight gives it, instead of its prefab from the effects pool, and the pool releases it. The launch position follows CreateView; a source bone is a point of the unit's model, which battles do not load, so the game's own fallback for a missing bone applies: the launcher's point one meter up, then the corpulence offset. Target offset, launch, flight, hit and miss are the game's. Cast effects, trails, the source bone's scale and the trajectory's visual shift of the view (TrajectoryCalculator.CalculateShift, an AnimationCurve) are skipped; the flight that decides the hit does not use the shift.");
            Declare("pathfinding-clock", "environment", "AstarPath.maxFrameTime and the time slice of A*'s path return queue while a battle runs",
                "The game's A* code limits its work per frame by the computer's clock: the scene's maxFrameTime in milliseconds of path searching, after which a search or the rest of the queue continues in the next frame, and 0.5 ms of handing finished paths back. The frame a path finishes in would depend on the machine's speed and load. A battle frame does all of it, as a machine fast enough for both limits does: maxFrameTime is 10^12 ms (the report keeps the scene's value), and the paths the time slice leaves go back right after A*'s Update, in the same frame and order. Paths requested during that hand-back are searched in the next frame, as in the game. Path results are the game's; which frame they finish in follows the game's order only.");
            Declare("frame-clock", "environment","Unity's Time (frameCount, realtimeSinceStartup, time, unscaledTime, deltaTime, unscaledDeltaTime, timeScale) while a battle runs its game loop",
                "Each battle frame is 1/30 s of real time. It starts as a Unity frame does: the frame count, real time and Unity's scaled and unscaled time advance before A* and the controllers run, and Time.deltaTime is the frame scaled by Time.timeScale. The game's own TimeController runs in the default mode: it sets Time.timeScale, the frame durations and game time, including its turn-based time scales. Between battle frames every read of the frame count is a new frame, so per-frame caches never return a value from before a change; outside battles these native calls fail as unity-runtime-required, as before.");
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
