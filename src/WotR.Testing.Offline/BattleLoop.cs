using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers;
using Kingmaker.Controllers.Combat;
using Kingmaker.Controllers.Units;
using Kingmaker.PubSubSystem;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The part of the game's default mode a battle runs. The controllers are the game's own, in the order
    /// GameModesFactory registers them, and are activated, started, ticked, deactivated and stopped as GameMode does.
    /// The frame clock (<see cref="BattleClock"/>) takes TimeController's place; the AstarPath component's Unity Update
    /// runs first in every frame. Controllers for rendering, sound, UI, camera, fog of war, AI, buffs and the rest of
    /// the default mode are not part of it.
    /// </summary>
    internal sealed class BattleLoop
    {
        private static readonly MethodInfo SetHandsEquipmentController = AccessTools.PropertySetter(typeof(Game), nameof(Game.HandsEquipmentController));
        private static readonly MethodInfo SetBoundsScene = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.Optimization.EntityBoundsController), "Scene");
        private static readonly MethodInfo SetBoundsPhysicsScene = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.Optimization.EntityBoundsController), "PhysicsScene");

        private readonly BattleNavigation navigation;
        private readonly UnitHandEquipmentController hands = new();
        private readonly IController[] controllers;
        private UnitHandEquipmentController previousHands;
        private UnityEngine.SceneManagement.Scene previousBoundsScene;
        private UnityEngine.PhysicsScene previousBoundsPhysicsScene;

        /// <summary>
        /// True from <see cref="Activate"/> to <see cref="Deactivate"/>. Only then do the physics-scene and frame-clock
        /// boundaries answer; outside a battle those native calls fail as unity-runtime-required, as before.
        /// </summary>
        internal static bool Running { get; private set; }

        internal BattleLoop(BattleNavigation navigation)
        {
            this.navigation = navigation;
            controllers = new IController[]
            {
                Game.Instance.UnitGroupsController,
                new UnitLineOfSightCacheController(),
                new UnitMoveController(),
                Game.Instance.EntityBoundsController,
                Game.Instance.UnitVisionRangeController,
                new SleepingUnitsController(),
                Game.Instance.UnitMemoryController,
                new UnitCombatJoinController(),
                new UnitCombatCooldownsController(),
                new UnitCombatEngagementController(),
                new UnitCommandController(),
                hands,
                new UnitCombatLeaveController(),
                new GarbageEntityController(),
            };
        }

        /// <summary>GameMode.OnActivate and OnStart: subscribe and activate every controller, then start them.</summary>
        internal void Activate()
        {
            previousHands = Game.Instance.HandsEquipmentController;
            previousBoundsScene = Game.Instance.EntityBoundsController.Scene;
            previousBoundsPhysicsScene = Game.Instance.EntityBoundsController.PhysicsScene;
            SetHandsEquipmentController.Invoke(Game.Instance, new object[] { hands });
            Boundaries.Hit("hand-equipment-controller");
            Running = true;
            foreach (var controller in controllers)
            {
                EventBus.Subscribe(controller);
                controller.Activate();
            }
            foreach (var controller in controllers) (controller as IControllerStart)?.Start();
        }

        /// <summary>One frame: A*'s Update, then the controllers in order, with the frame clock in TimeController's place.</summary>
        internal void Frame()
        {
            navigation.Update();
            controllers[0].Tick();
            BattleClock.Frame();
            for (var i = 1; i < controllers.Length; i++) controllers[i].Tick();
        }

        /// <summary>
        /// GameMode.OnDeactivate and OnStop. Like GameMode, one controller's exception does not keep the others active;
        /// the exceptions are thrown together afterwards.
        /// </summary>
        internal void Deactivate()
        {
            var errors = new System.Collections.Generic.List<System.Exception>();
            foreach (var controller in controllers)
            {
                EventBus.Unsubscribe(controller);
                try { controller.Deactivate(); }
                catch (System.Exception error) { errors.Add(error); }
            }
            foreach (var controller in controllers)
            {
                try { (controller as IControllerStop)?.Stop(); }
                catch (System.Exception error) { errors.Add(error); }
            }
            SetHandsEquipmentController.Invoke(Game.Instance, new object[] { previousHands });
            // The entity-bounds scene exists only while the battle runs; the game keeps it for the session.
            SetBoundsScene.Invoke(Game.Instance.EntityBoundsController, new object[] { previousBoundsScene });
            SetBoundsPhysicsScene.Invoke(Game.Instance.EntityBoundsController, new object[] { previousBoundsPhysicsScene });
            Running = false;
            if (errors.Count > 0) throw new System.AggregateException("Stopping the battle's game controllers failed.", errors);
        }
    }
}
