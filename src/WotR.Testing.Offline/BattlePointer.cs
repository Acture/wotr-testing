using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers.Clicks;
using Kingmaker.Controllers.Clicks.Handlers;
using Kingmaker.PubSubSystem;
using Kingmaker.TurnBasedMode;
using Kingmaker.UI.Selection;
using UnityEngine;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The player's mouse in a battle. The game's own PointerController is the game's pointer, as the default mode's
    /// public click controller (<c>Game.ClickEventsController</c>), but it does not tick: there is no mouse, camera or
    /// level to cast rays into. The battle environment moves it instead (<see cref="PointAt"/>): onto a unit's view, or
    /// onto a point of the ground, which has no object because no level is loaded. Then it clicks (<see cref="Click"/>)
    /// as PointerController.Tick handles a left click: the click handler of the pointed object, which the game chooses by
    /// its priorities, gets the click, and in turn-based combat the current turn may ignore it first. The game's code
    /// that reads the pointer (the turn-based predictions, the path preview, the click handlers) runs unchanged.
    /// </summary>
    internal static class BattlePointer
    {
        private static readonly MethodInfo SetClickEvents = AccessTools.PropertySetter(typeof(Game), nameof(Game.ClickEventsController));
        private static readonly MethodInfo SetSelectedAbilityHandler = AccessTools.PropertySetter(typeof(Game), nameof(Game.SelectedAbilityHandler));
        private static readonly MethodInfo SetPlaceRestMarkerHandler = AccessTools.PropertySetter(typeof(Game), nameof(Game.PlaceRestMarkerHandler));
        private static readonly MethodInfo SetPointerOn = AccessTools.PropertySetter(typeof(PointerController), nameof(PointerController.PointerOn));
        private static readonly MethodInfo SetWorldPosition = AccessTools.PropertySetter(typeof(PointerController), nameof(PointerController.WorldPosition));
        private static readonly MethodInfo HoverChanged = AccessTools.Method(typeof(PointerController), "OnHoverChanged");
        private static readonly FieldInfo Handlers = AccessTools.Field(typeof(PointerController), "m_ClickHandlers");
        private static readonly FieldInfo SimulateClickHandler = AccessTools.Field(typeof(PointerController), "m_SimulateClickHandler");
        private static readonly FieldInfo WorldPositionForSimulation = AccessTools.Field(typeof(PointerController), "m_WorldPositionForSimulation");
        private static readonly FieldInfo MouseDownHandler = AccessTools.Field(typeof(PointerController), "m_MouseDownHandler");

        private static PointerController previousClickEvents;
        private static ClickWithSelectedAbilityHandler previousAbilityHandler;
        private static PlaceRestMarkerHandler previousRestHandler;
        private static bool installed;

        /// <summary>What <c>Game.SetupPublicControllers</c> sets from the default mode's pointer controller.</summary>
        internal static void Install()
        {
            var game = Game.Instance;
            previousClickEvents = game.ClickEventsController;
            previousAbilityHandler = game.SelectedAbilityHandler;
            previousRestHandler = game.PlaceRestMarkerHandler;
            var pointer = game.DefaultPointerController;
            SetClickEvents.Invoke(game, new object[] { pointer });
            SetSelectedAbilityHandler.Invoke(game, new object[] { pointer.GetHandler<ClickWithSelectedAbilityHandler>() });
            SetPlaceRestMarkerHandler.Invoke(game, new object[] { pointer.GetHandler<PlaceRestMarkerHandler>() });
            installed = true;
            Boundaries.Hit("pointer");
        }

        /// <summary>
        /// The pointer moves off the object it is on, onto the ground where it is, as a mouse leaving a unit does; the game
        /// hears it while the battle's units still exist. Battle cleanup does this before the units are destroyed.
        /// </summary>
        internal static void Leave()
        {
            var pointer = Game.Instance.DefaultPointerController;
            if (installed && pointer.PointerOn != null) PointAt(null, pointer.WorldPosition);
        }

        /// <summary>The pointer leaves the battle hovering nothing (<see cref="Leave"/>), in its default mode.</summary>
        internal static void Remove()
        {
            if (!installed) return;
            var game = Game.Instance;
            try
            {
                game.DefaultPointerController.Deactivate();
            }
            finally
            {
                SetClickEvents.Invoke(game, new object[] { previousClickEvents });
                SetSelectedAbilityHandler.Invoke(game, new object[] { previousAbilityHandler });
                SetPlaceRestMarkerHandler.Invoke(game, new object[] { previousRestHandler });
                previousClickEvents = null;
                previousAbilityHandler = null;
                previousRestHandler = null;
                installed = false;
            }
        }

        /// <summary>
        /// Moves the pointer onto <paramref name="target"/> (a stand-in view's GameObject) or, with no target, onto the
        /// ground at <paramref name="worldPosition"/>, as PointerController.Tick does after its raycast: the handler that
        /// would take a click there is chosen by the game's priorities, the position becomes the pointer's, and a change of
        /// the hovered object is announced to the game (IMouseHoverHandler, which the turn-based predictions follow).
        /// </summary>
        internal static void PointAt(GameObject target, Vector3 worldPosition)
        {
            var pointer = Game.Instance.DefaultPointerController;
            var handler = HandlerFor(pointer, target, worldPosition);
            SimulateClickHandler.SetValue(pointer, handler);
            WorldPositionForSimulation.SetValue(pointer, pointer.WorldPosition);
            SetWorldPosition.Invoke(pointer, new object[] { worldPosition });
            var previous = pointer.PointerOn;
            if (!ReferenceEquals(previous, target))
            {
                HoverChanged.Invoke(pointer, new object[] { previous, target });
                SetPointerOn.Invoke(pointer, new object[] { target });
            }
            Boundaries.Hit("pointer");
        }

        /// <summary>
        /// A left click where the pointer is, as PointerController.Tick handles the button's release: in turn-based combat
        /// the current turn may ignore it (IgnoreClick); otherwise the chosen handler's OnClick runs. Returns whether a
        /// handler took the click.
        /// </summary>
        internal static bool Click()
        {
            var pointer = Game.Instance.DefaultPointerController;
            var handler = (IClickEventHandler)SimulateClickHandler.GetValue(pointer);
            MouseDownHandler.SetValue(pointer, handler);
            Boundaries.Hit("pointer");
            if (handler == null) return false;
            if (TurnBased.Controllers.CombatController.IsInTurnBasedCombat()
                && (Game.Instance.TurnBasedCombatController.CurrentTurn?.IgnoreClick() ?? true))
                return false;
            var worldPosition = pointer.WorldPosition;
            if (!handler.OnClick(pointer.PointerOn, worldPosition, 0)) return false;
            EventBus.RaiseEvent((IClickMarkHandler h) => h.OnClickHandled(worldPosition));
            return true;
        }

        /// <summary>The first handler of the pointer's mode with a positive priority for the object, as the game picks one.</summary>
        private static IClickEventHandler HandlerFor(PointerController pointer, GameObject target, Vector3 worldPosition)
        {
            foreach (var handler in (IClickEventHandler[])Handlers.GetValue(pointer))
            {
                if (handler.GetMode() != pointer.Mode) continue;
                var priority = handler is IClickAndOvertipEventHandler overtip && target != null
                    ? overtip.ProcessObject(target, worldPosition).Priority
                    : handler.GetPriority(target, worldPosition);
                if (priority > 0f) return handler;
            }
            return null;
        }
    }

    /// <summary>
    /// The UI's selection manager while a battle runs: the game's click handlers ask it which selected unit acts. The
    /// selection itself is the game's state (<c>Game.SelectionCharacter</c>, which the turn-based TurnController sets to
    /// the unit whose turn it is); the manager answers from it. A battle gets a SelectionManagerPC without a GameObject,
    /// as its Awake registers it. Its selection marks are never created and it does not follow party changes, which it
    /// handles with coroutines on the UI.
    /// </summary>
    internal static class SelectionManagerStandIn
    {
        private static readonly MethodInfo Awake = AccessTools.Method(typeof(Kingmaker.UI.Selection.SelectionManagerPC), "Awake");
        private static readonly MethodInfo SetInstance = AccessTools.PropertySetter(typeof(SelectionManagerBase), nameof(SelectionManagerBase.Instance));

        private static Kingmaker.UI.Selection.SelectionManagerPC standIn;

        internal static object Current => SelectionManagerBase.Instance;

        internal static void Install()
        {
            if (SelectionManagerBase.Instance is { } existing && existing)
                throw new InvalidOperationException("The game already has a selection manager; a battle runs without the game's UI.");
            standIn = (Kingmaker.UI.Selection.SelectionManagerPC)Activator.CreateInstance(typeof(Kingmaker.UI.Selection.SelectionManagerPC), nonPublic: true);
            OfflineUnitView.MarkLive(standIn);
            Awake.Invoke(standIn, null);
            Boundaries.Hit("selection-ui");
        }

        internal static void Remove()
        {
            if (standIn is null) return;
            if (ReferenceEquals(SelectionManagerBase.Instance, standIn)) SetInstance.Invoke(null, new object[] { null });
            OfflineUnitView.MarkDead(standIn);
            standIn = null;
        }
    }

    /// <summary>
    /// The turn-based path preview, <c>PathVisualizer</c>, while a battle runs. The game's UI holds it on a GameObject
    /// with a line renderer and two decals; the turn-based predictions ask it for the path of the command under the
    /// pointer, and its movement simulation shortens a move to the movement left. A battle gets one without a GameObject,
    /// whose line renderer and decals are stand-ins without native objects. Its Update runs every battle frame, as Unity
    /// runs it; only drawing the line (DecimateLine, SetGradientsToRenderer: points, colors, materials and decal
    /// positions) is skipped.
    /// </summary>
    internal static class PathVisualizerStandIn
    {
        private static readonly MethodInfo SetInstance = AccessTools.PropertySetter(typeof(PathVisualizer), nameof(PathVisualizer.Instance));
        private static readonly FieldInfo Renderer = AccessTools.Field(typeof(PathVisualizer), "m_Renderer");
        private static readonly FieldInfo MoveDecal = AccessTools.Field(typeof(PathVisualizer), "m_BreakMoveDecal");
        private static readonly FieldInfo StandardDecal = AccessTools.Field(typeof(PathVisualizer), "m_BreakStandardDecal");
        private static readonly MethodInfo UpdateMethod = AccessTools.Method(typeof(PathVisualizer), "Update");

        private static PathVisualizer standIn;
        private static readonly List<Component> Parts = new();

        internal static object Current => PathVisualizer.Instance;

        internal static void Install()
        {
            if (PathVisualizer.Instance is { } existing && existing)
                throw new InvalidOperationException("The game already has a PathVisualizer; a battle runs without the game's UI.");
            standIn = (PathVisualizer)Activator.CreateInstance(typeof(PathVisualizer), nonPublic: true);
            OfflineUnitView.MarkLive(standIn);
            // What Awake does: the instance, its event subscriptions and the line renderer it instantiates.
            foreach (var (field, type) in new[] { (Renderer, typeof(LineRenderer)), (MoveDecal, typeof(MeshRenderer)), (StandardDecal, typeof(MeshRenderer)) })
            {
                var part = OfflineUnitView.CreateDetached(type);
                Parts.Add(part);
                field.SetValue(standIn, part);
            }
            SetInstance.Invoke(null, new object[] { standIn });
            EventBus.Subscribe(standIn);
            Boundaries.Hit("path-preview");
        }

        internal static void Update()
        {
            if (standIn is null) return;
            UpdateMethod.Invoke(standIn, null);
        }

        /// <summary>What OnDestroy does, and the stand-in and its parts read as destroyed.</summary>
        internal static void Remove()
        {
            if (standIn is null) return;
            try
            {
                EventBus.Unsubscribe(standIn);
            }
            finally
            {
                if (ReferenceEquals(PathVisualizer.Instance, standIn)) SetInstance.Invoke(null, new object[] { null });
                foreach (var part in Parts) OfflineUnitView.ReleaseDetached(part);
                Parts.Clear();
                OfflineUnitView.MarkDead(standIn);
                standIn = null;
            }
        }
    }

    /// <summary>
    /// Drawing the turn-based path preview's line while a battle runs: see <see cref="PathVisualizerStandIn"/>. Applied
    /// after the attribute patches: compiling the replacement of these methods initializes the game's ShaderProps, which
    /// asks Unity for shader ids, so the shader-ids boundary must be in place first.
    /// </summary>
    internal static class PathPreviewDrawingPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var skip = new HarmonyMethod(AccessTools.Method(typeof(PathPreviewDrawingPatches), nameof(Skip)));
            foreach (var name in new[] { "DecimateLine", "SetGradientsToRenderer" })
                harmony.Patch(AccessTools.Method(typeof(PathVisualizer), name), prefix: skip);
        }

        private static bool Skip()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("path-preview");
            return false;
        }
    }

    /// <summary>The points of a stand-in line renderer, kept as Unity keeps them for a native one.</summary>
    [HarmonyPatch(typeof(LineRenderer))]
    internal static class StandInLinePatches
    {
        [HarmonyPatch("get_positionCount"), HarmonyPrefix]
        private static bool GetCount(LineRenderer __instance, ref int __result)
        {
            if (OfflineUnitView.LinePoints(__instance) is not { } points) return true;
            __result = points.Count;
            return false;
        }

        [HarmonyPatch("set_positionCount"), HarmonyPrefix]
        private static bool SetCount(LineRenderer __instance, int value)
        {
            if (OfflineUnitView.LinePoints(__instance) is not { } points) return true;
            var count = Math.Max(0, value);
            if (points.Count > count) points.RemoveRange(count, points.Count - count);
            while (points.Count < count) points.Add(Vector3.zero);
            return false;
        }

        [HarmonyPatch(nameof(LineRenderer.GetPositions), typeof(Vector3[])), HarmonyPrefix]
        private static bool GetPositions(LineRenderer __instance, Vector3[] positions, ref int __result)
        {
            if (OfflineUnitView.LinePoints(__instance) is not { } points) return true;
            var count = Math.Min(points.Count, positions.Length);
            for (var i = 0; i < count; i++) positions[i] = points[i];
            __result = count;
            return false;
        }

        [HarmonyPatch(nameof(LineRenderer.SetPositions), typeof(Vector3[])), HarmonyPrefix]
        private static bool SetPositions(LineRenderer __instance, Vector3[] positions)
        {
            if (OfflineUnitView.LinePoints(__instance) is not { } points) return true;
            points.Clear();
            points.AddRange(positions);
            return false;
        }
    }

    /// <summary>
    /// The mouse cursor's image while a battle runs: the game's CursorController decides which cursor to show as usual,
    /// but setting the image (its private SetCursor) is skipped, as there is no screen.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorImagePatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.GetDeclaredMethods(typeof(Kingmaker.UI.CursorController)).Single(m => m.Name == "SetCursor" && m.IsPrivate);

        private static bool Prefix()
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("cursor");
            return false;
        }
    }

    /// <summary>
    /// Unity's legacy Input while a battle runs: there is no keyboard or mouse, so no key or button is held, pressed or
    /// released, axes are at rest and the mouse position is the origin. The game's code that checks for modifier keys (for
    /// example Ctrl to force a move) takes its branch for no key held.
    /// </summary>
    [HarmonyPatch]
    internal static class BattleInputPatches
    {
        private static readonly Type InputType = typeof(UnityEngine.Input);

        private static IEnumerable<MethodBase> TargetMethods()
            => new[]
            {
                "GetKeyInt", "GetKeyString", "GetKeyUpInt", "GetKeyUpString", "GetKeyDownInt", "GetKeyDownString",
                "GetButton", "GetButtonDown", "GetButtonUp", "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp",
                "get_anyKey", "get_anyKeyDown",
            }.Select(name => (MethodBase)AccessTools.Method(InputType, name)).Where(m => m != null);

        private static bool Prefix(ref bool __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("input");
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class BattleInputAxisPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var input = typeof(UnityEngine.Input);
            return new[] { "GetAxis", "GetAxisRaw" }.Select(name => (MethodBase)AccessTools.Method(input, name)).Where(m => m != null);
        }

        private static bool Prefix(ref float __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("input");
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class BattleMousePositionPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(UnityEngine.Input), "get_mousePosition_Injected");

        private static bool Prefix(out Vector3 ret)
        {
            ret = Vector3.zero;
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("input");
            return false;
        }
    }
}
