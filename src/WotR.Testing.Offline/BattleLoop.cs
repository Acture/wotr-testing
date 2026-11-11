using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers;
using Kingmaker.Controllers.Combat;
using Kingmaker.Controllers.Units;
using Kingmaker.GameModes;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The game's default mode as a battle runs it. <c>GameModesFactory.Create(GameModeType.Default)</c> gives the
    /// controllers and their order; the battle leaves out only the presentation controllers in <see cref="Excluded"/>.
    /// The mode is started and stopped as <c>Game.DoStartMode</c> and <c>DoStopMode</c> do: pushed on the game's mode
    /// stack, so <c>Game.CurrentMode</c> is Default and <c>Game.GetController</c> finds its controllers, started, then
    /// activated. Every frame starts as a Unity frame does (<see cref="BattleClock"/>, then the AstarPath component's
    /// Update) and runs what <c>Game.Tick</c> runs for the mechanics: the scheduled actions, the real-time clock, the
    /// mode's own <c>GameMode.Tick</c> and the temporary entities.
    /// </summary>
    internal sealed class BattleLoop
    {
        /// <summary>Controllers of the default mode a battle leaves out, by type, with the reason.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Kingmaker.UI.KeyboardAccess"] = "keyboard input",
            ["Kingmaker.Controllers.Clicks.PointerController"] = "mouse input: clicks, hover and the cursor",
            ["Kingmaker.Controllers.UnpauseController"] = "the player's pause and unpause",
            ["Kingmaker.Controllers.AutoPauseController"] = "pausing the game for the player on events",
            ["Kingmaker.Controllers.MapObjects.InteractionHighlightController"] = "highlighting objects the player can use",
            ["Kingmaker.Controllers.Units.UnitPlaceOnGroundController"] = "raycasts against level colliders, which battles do not load; units stand at navigation-mesh height",
            ["Kingmaker.Controllers.FogOfWarController"] = "fog of war, rendered on the GPU",
            ["Kingmaker.Visual.Particles.FxSpawnSystem.FxSpawnController"] = "visual effects",
            ["Kingmaker.Controllers.Units.UnitAnimationController"] = "animations; there is no animator, so commands act on their first tick",
            ["Kingmaker.Controllers.FootprintsController"] = "footprint decals",
            ["Kingmaker.Controllers.WeatherController"] = "weather visuals",
            ["Kingmaker.Controllers.Rest.CameraController"] = "the camera",
            ["Kingmaker.Controllers.Optimization.EntitiesInCameraFrustumController"] = "which entities the camera sees",
            ["Kingmaker.Controllers.InspectUnitsController"] = "the unit inspection window",
            ["Kingmaker.Controllers.Combat.GameOverController"] = "ending the session with the game-over screen when the party falls; a battle is decided by its consumer",
            ["Kingmaker.Dungeon.Leaderboard.DungeonLeaderboardController"] = "the online dungeon leaderboard",
            ["Kingmaker.Controllers.ClockworkController"] = "the game's QA automation",
            ["Kingmaker.Controllers.DlcSaveImporterController"] = "importing saves for DLCs",
            ["Kingmaker.Tutorial.TutorialController"] = "tutorial windows",
            ["Kingmaker.Controllers.SelectionCharacterController"] = "the player's selection of characters",
            ["Kingmaker.UI.Models.Log.GameLogController"] = "the combat log window",
        };

        private static readonly FieldInfo GameModes = AccessTools.Field(typeof(Game), "m_GameModes");
        private static readonly FieldInfo ModeControllers = AccessTools.Field(typeof(GameMode), "m_Controllers");
        private static readonly PropertyInfo ModesCount = AccessTools.Property(typeof(Game), "ModesCount");
        private static readonly MethodInfo ClearPublicControllers = AccessTools.Method(typeof(Game), "ClearPublicControllers");
        private static readonly MethodInfo HandleGameModeChanged = AccessTools.Method(typeof(Game), "HandleGameModeChanged");
        private static readonly MethodInfo ExecuteBeforeTickActions = AccessTools.Method(typeof(Game), "ExecuteBeforeTickActions");
        private static readonly MethodInfo SetHandsEquipmentController = AccessTools.PropertySetter(typeof(Game), nameof(Game.HandsEquipmentController));
        private static readonly MethodInfo SetCombatEngagementController = AccessTools.PropertySetter(typeof(Game), nameof(Game.CombatEngagementController));
        private static readonly MethodInfo SetCameraController = AccessTools.PropertySetter(typeof(Game), nameof(Game.CameraController));
        private static readonly MethodInfo SetBoundsScene = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.Optimization.EntityBoundsController), "Scene");
        private static readonly MethodInfo SetBoundsPhysicsScene = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.Optimization.EntityBoundsController), "PhysicsScene");

        private readonly BattleNavigation navigation;
        private readonly GameMode mode;
        private UnitHandEquipmentController previousHands;
        private UnitCombatEngagementController previousEngagement;
        private Kingmaker.Controllers.Rest.CameraController previousCamera;
        private readonly Kingmaker.Controllers.Rest.CameraController camera;
        private UnityEngine.SceneManagement.Scene previousBoundsScene;
        private UnityEngine.PhysicsScene previousBoundsPhysicsScene;
        private bool started;

        /// <summary>
        /// True from <see cref="Activate"/> to <see cref="Deactivate"/>. Only then do the physics-scene and frame-clock
        /// boundaries answer; outside a battle those native calls fail as unity-runtime-required, as before.
        /// </summary>
        internal static bool Running { get; private set; }

        internal BattleLoop(BattleNavigation navigation)
        {
            this.navigation = navigation;
            var all = (IController[])ModeControllers.GetValue(GameModesFactory.Create(GameModeType.Default));
            Controllers = all.Where(c => !Excluded.ContainsKey(c.GetType().FullName)).ToArray();
            camera = all.OfType<Kingmaker.Controllers.Rest.CameraController>().Single();
            mode = new GameMode(GameModeType.Default, Controllers);
        }

        /// <summary>The controllers the battle runs, in the game's order.</summary>
        internal IReadOnlyList<IController> Controllers { get; }

        private static Stack<GameMode> Modes => (Stack<GameMode>)GameModes.GetValue(Game.Instance);

        /// <summary>
        /// Game.DoStartMode(Default) from no mode: the public controllers are cleared, the mode is pushed, started and
        /// activated, the public controllers the mechanics use are set from it, and the mode change is announced.
        /// </summary>
        internal void Activate()
        {
            var game = Game.Instance;
            if (game.CurrentMode != GameModeType.None)
                throw new InvalidOperationException($"The game is in mode {game.CurrentMode}; a battle starts the default mode when no mode runs.");
            previousHands = game.HandsEquipmentController;
            previousEngagement = game.CombatEngagementController;
            previousCamera = game.CameraController;
            previousBoundsScene = game.EntityBoundsController.Scene;
            previousBoundsPhysicsScene = game.EntityBoundsController.PhysicsScene;
            Running = true;
            LoadingProcessStandIn.Install();
            MainThreadDispatcherStandIn.Install();
            ClearPublicControllers.Invoke(game, null);
            Count(+1);
            Modes.Push(mode);
            started = true;
            mode.OnStart(null);
            mode.OnActivate();
            // SetupPublicControllers for the default mode; the pointer, its click handlers and the interaction highlight
            // and camera controllers are not part of a battle.
            SetHandsEquipmentController.Invoke(game, new object[] { mode.GetController<UnitHandEquipmentController>() });
            SetCombatEngagementController.Invoke(game, new object[] { mode.GetController<UnitCombatEngagementController>() });
            // The mode's camera controller is the game's, for code that follows or releases a unit with the camera; it
            // neither activates nor ticks, so no camera rig is read or moved.
            SetCameraController.Invoke(game, new object[] { camera });
            Boundaries.Hit("camera");
            // The pointer controller and the turn-based path preview, which the mode's controllers read; neither ticks on
            // its own (see BattlePointer and PathVisualizerStandIn).
            BattlePointer.Install();
            SelectionManagerStandIn.Install();
            PathVisualizerStandIn.Install();
            Boundaries.Hit("game-mode");
            HandleGameModeChanged.Invoke(game, new object[] { GameModeType.None, GameModeType.Default });
        }

        /// <summary>One frame: Unity's frame clock, A*'s Update, then Game.Tick's scheduled actions, real time, mode and temporary entities.</summary>
        internal void Frame()
        {
            BattleClock.BeginFrame();
            navigation.Update();
            var game = Game.Instance;
            ExecuteBeforeTickActions.Invoke(game, null);
            game.TimeController.TickRealTime();
            mode.Tick();
            Kingmaker.EntitySystem.TemporaryEntityService.Instance?.Tick();
            PathVisualizerStandIn.Update();
            MainThreadDispatcherStandIn.Update();
        }

        /// <summary>
        /// Game.DoStopMode(Default): the public controllers are cleared, the mode is popped, deactivated and stopped, and
        /// the mode change is announced. The game state the battle replaced is restored in any case; exceptions are thrown
        /// together afterwards.
        /// </summary>
        internal void Deactivate()
        {
            var errors = new List<Exception>();
            void Step(Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); }
            }

            var game = Game.Instance;
            if (started)
            {
                Step(() => ClearPublicControllers.Invoke(game, null));
                Step(() =>
                {
                    if (Modes.Count > 0 && ReferenceEquals(Modes.Peek(), mode)) Modes.Pop();
                    else throw new InvalidOperationException($"The battle's default mode is not on top of the game's mode stack (current mode {game.CurrentMode}).");
                });
                Step(() => Count(-1));
                Step(mode.OnDeactivate);
                Step(() => mode.OnStop(null));
                Step(() => HandleGameModeChanged.Invoke(game, new object[] { GameModeType.Default, game.CurrentMode }));
                started = false;
            }
            Step(() => SetHandsEquipmentController.Invoke(game, new object[] { previousHands }));
            Step(() => SetCombatEngagementController.Invoke(game, new object[] { previousEngagement }));
            Step(() => SetCameraController.Invoke(game, new object[] { previousCamera }));
            Step(PathVisualizerStandIn.Remove);
            Step(SelectionManagerStandIn.Remove);
            Step(BattlePointer.Remove);
            // The entity-bounds scene exists only while the battle runs; the game keeps it for the session.
            Step(() => SetBoundsScene.Invoke(game.EntityBoundsController, new object[] { previousBoundsScene }));
            Step(() => SetBoundsPhysicsScene.Invoke(game.EntityBoundsController, new object[] { previousBoundsPhysicsScene }));
            Step(LoadingProcessStandIn.Remove);
            Step(MainThreadDispatcherStandIn.Remove);
            Running = false;
            if (errors.Count > 0) throw new AggregateException("Stopping the battle's game mode failed.", errors);
        }

        private static void Count(int change)
        {
            var counts = (int[])ModesCount.GetValue(Game.Instance);
            counts[(int)GameModeType.Default] += change;
        }
    }

    /// <summary>
    /// A battle runs the default mode only. When the game starts or stops another mode during a battle (a dialog, a
    /// cutscene, the pause or game over), the frame fails with that mode named; nothing switches.
    /// </summary>
    [HarmonyPatch(typeof(Game))]
    internal static class BattleModeGuard
    {
        [HarmonyPatch("DoStartMode"), HarmonyPrefix]
        private static void Start(GameModeType type)
        {
            if (BattleLoop.Running) throw new NotSupportedException($"The game started game mode {type} during a battle; battles run the default mode only.");
        }

        [HarmonyPatch("DoStopMode"), HarmonyPrefix]
        private static void Stop(GameModeType type)
        {
            if (BattleLoop.Running) throw new NotSupportedException($"The game stopped game mode {type} during a battle; battles run the default mode only.");
        }
    }

    /// <summary>
    /// The Isles' camera direction: <c>IslesController.TickCameraRotation</c> reads the camera rig's rotation and announces
    /// the direction it faces. No camera exists in a battle; the isles themselves still tick.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.AreaLogic.AlushenyrraIsles.IslesController), "TickCameraRotation")]
    internal static class IslesCameraPatch
    {
        private static bool Prefix()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("camera");
            return false;
        }
    }
}
