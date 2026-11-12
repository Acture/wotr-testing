using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Persistence;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The game's loading process while a battle runs. LoadingProcess is a component the game creates on a new
    /// GameObject the first time it is asked for; a battle loads no scenes and shows no loading screens, so it gets a
    /// LoadingProcess without a GameObject for its duration. The game's code asks it as usual (for example the turn-based
    /// CombatController resets the manual loading screen when combat starts) and gets its own answers; its Update and
    /// coroutines never run.
    /// </summary>
    internal static class LoadingProcessStandIn
    {
        private static readonly FieldInfo InstanceField = AccessTools.Field(typeof(LoadingProcess), "s_Instance")
            ?? throw new InvalidOperationException("LoadingProcess.s_Instance is not readable on this game version.");

        private static LoadingProcess standIn;

        internal static void Install()
        {
            if (InstanceField.GetValue(null) is LoadingProcess existing && existing)
                throw new InvalidOperationException("The game already has a LoadingProcess; a battle runs without one.");
            standIn = (LoadingProcess)Activator.CreateInstance(typeof(LoadingProcess), nonPublic: true);
            OfflineUnitView.MarkLive(standIn);
            InstanceField.SetValue(null, standIn);
            Boundaries.Hit("loading-process");
        }

        internal static void Remove()
        {
            if (standIn is null) return;
            if (ReferenceEquals(InstanceField.GetValue(null), standIn)) InstanceField.SetValue(null, null);
            OfflineUnitView.MarkDead(standIn);
            standIn = null;
        }

        internal static object Current => InstanceField.GetValue(null);
    }

    /// <summary>
    /// UniRx's MainThreadDispatcher while a battle runs: the game posts actions to the main thread with it (for example
    /// the turn-based CombatController when combat ends), and Unity runs them in the dispatcher's Update on a later
    /// frame. The game would create the dispatcher on a new GameObject; a battle gets one without a GameObject for its
    /// duration, and its own Update runs at the end of every battle frame and once when the battle stops, as the next
    /// frame would. Its coroutines (micro-coroutines, end of frame) are not started.
    /// </summary>
    internal static class MainThreadDispatcherStandIn
    {
        private static readonly Type DispatcherType = AccessTools.TypeByName("UniRx.MainThreadDispatcher")
            ?? throw new InvalidOperationException("UniRx.MainThreadDispatcher is not loaded on this game version.");
        private static readonly FieldInfo InstanceField = Field("instance");
        private static readonly FieldInfo InitializedField = Field("initialized");
        private static readonly FieldInfo TokenField = Field("mainThreadToken");
        private static readonly MethodInfo UpdateMethod = AccessTools.Method(DispatcherType, "Update")
            ?? throw new InvalidOperationException("UniRx.MainThreadDispatcher.Update is not readable on this game version.");

        private static UnityEngine.Object standIn;
        private static object previousToken;

        private static FieldInfo Field(string name) => AccessTools.Field(DispatcherType, name)
            ?? throw new InvalidOperationException($"UniRx.MainThreadDispatcher.{name} is not readable on this game version.");

        internal static object Current => InstanceField.GetValue(null);

        internal static void Install()
        {
            if (InstanceField.GetValue(null) is UnityEngine.Object existing && existing)
                throw new InvalidOperationException("The game already has a MainThreadDispatcher; a battle runs without one.");
            standIn = (UnityEngine.Object)Activator.CreateInstance(DispatcherType, nonPublic: true);
            OfflineUnitView.MarkLive(standIn);
            previousToken = TokenField.GetValue(null);
            // What the dispatcher's Awake records about itself, without its coroutines or DontDestroyOnLoad.
            InstanceField.SetValue(null, standIn);
            InitializedField.SetValue(null, true);
            TokenField.SetValue(null, previousToken ?? new object());
            Boundaries.Hit("main-thread-dispatcher");
        }

        /// <summary>The dispatcher's own Update: its update subjects and the actions posted since the last frame.</summary>
        internal static void Update()
        {
            if (standIn is null) return;
            UpdateMethod.Invoke(standIn, null);
        }

        internal static void Remove()
        {
            if (standIn is null) return;
            try
            {
                Update();
            }
            finally
            {
                if (ReferenceEquals(InstanceField.GetValue(null), standIn))
                {
                    InstanceField.SetValue(null, null);
                    InitializedField.SetValue(null, false);
                    TokenField.SetValue(null, previousToken);
                }
                OfflineUnitView.MarkDead(standIn);
                standIn = null;
                previousToken = null;
            }
        }
    }

    /// <summary>
    /// The camera's scroll to the unit whose turn starts: <c>TurnController.TryScrollToUnit</c> starts the camera rig's
    /// scroll, and the turn waits for it in its Scrolling status. No camera exists in a battle, so nothing scrolls and
    /// the turn is prepared at once, as when the player switches the scroll off in the game's settings.
    /// </summary>
    [HarmonyPatch(typeof(TurnBased.Controllers.TurnController), nameof(TurnBased.Controllers.TurnController.TryScrollToUnit))]
    internal static class TurnCameraScrollPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("camera");
            __result = false;
            return false;
        }
    }
}
