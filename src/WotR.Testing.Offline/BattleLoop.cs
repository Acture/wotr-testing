using System;
using System.Collections.Generic;
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
    /// The frame clock (<see cref="BattleClock"/>) takes TimeController's place. Every frame starts as a Unity frame does:
    /// the frame count and real time advance, then the AstarPath component's Unity Update runs, then the controllers.
    /// Controllers for rendering, sound, UI, camera, fog of war, AI, buffs and the rest of the default mode are not part
    /// of it.
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
        private bool replaced;
        private int subscribed;
        private int activated;
        private int started;

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

        /// <summary>
        /// GameMode.OnActivate and OnStart: subscribe and activate every controller, then start them. If one throws, the
        /// loop counts how far it got, so <see cref="Deactivate"/> undoes exactly that.
        /// </summary>
        internal void Activate()
        {
            previousHands = Game.Instance.HandsEquipmentController;
            previousBoundsScene = Game.Instance.EntityBoundsController.Scene;
            previousBoundsPhysicsScene = Game.Instance.EntityBoundsController.PhysicsScene;
            SetHandsEquipmentController.Invoke(Game.Instance, new object[] { hands });
            replaced = true;
            Boundaries.Hit("hand-equipment-controller");
            Running = true;
            BattleClock.Activate();
            foreach (var controller in controllers)
            {
                EventBus.Subscribe(controller);
                subscribed++;
                controller.Activate();
                activated++;
            }
            foreach (var controller in controllers)
            {
                (controller as IControllerStart)?.Start();
                started++;
            }
        }

        /// <summary>One frame: Unity's frame clock, A*'s Update, then the controllers in order, with TimeController's tick in its place.</summary>
        internal void Frame()
        {
            BattleClock.BeginFrame();
            navigation.Update();
            controllers[0].Tick();
            BattleClock.Tick();
            for (var i = 1; i < controllers.Length; i++) controllers[i].Tick();
        }

        /// <summary>
        /// GameMode.OnDeactivate and OnStop for the controllers <see cref="Activate"/> reached. Like GameMode, one
        /// controller's exception does not keep the others active, and the game state the loop replaced is restored in
        /// any case; the exceptions are thrown together afterwards.
        /// </summary>
        internal void Deactivate()
        {
            var errors = new List<Exception>();
            void Step(Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); }
            }

            for (var i = 0; i < subscribed; i++)
            {
                var controller = controllers[i];
                Step(() => EventBus.Unsubscribe(controller));
                if (i < activated) Step(controller.Deactivate);
            }
            for (var i = 0; i < started; i++)
            {
                var controller = controllers[i];
                Step(() => (controller as IControllerStop)?.Stop());
            }
            subscribed = activated = started = 0;
            if (replaced)
            {
                Step(() => SetHandsEquipmentController.Invoke(Game.Instance, new object[] { previousHands }));
                // The entity-bounds scene exists only while the battle runs; the game keeps it for the session.
                Step(() => SetBoundsScene.Invoke(Game.Instance.EntityBoundsController, new object[] { previousBoundsScene }));
                Step(() => SetBoundsPhysicsScene.Invoke(Game.Instance.EntityBoundsController, new object[] { previousBoundsPhysicsScene }));
                replaced = false;
            }
            Running = false;
            if (errors.Count > 0) throw new AggregateException("Stopping the battle's game controllers failed.", errors);
        }
    }
}
