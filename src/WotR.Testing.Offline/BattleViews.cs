using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View;
using UnityEngine;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Gives battle units a <see cref="UnitEntityView"/> without a GameObject, model or animator. The game runs commands
    /// only for units whose view is live (<c>UnitCommandController</c> skips <c>View == null</c>), so the stand-in is
    /// marked live for Unity's checks. The prefab is the one the game picks (an active polymorph's, else
    /// <see cref="UnitViewPrefab.For"/>), and the stand-in is made as Unity's Instantiate makes a view: it copies the
    /// serialized fields of the prefab's UnitEntityView (corpulence, rotation lock), OverrideSizeScale and
    /// UnitMovementAgent (acceleration, speeds, avoidance). The game's own <c>AttachView</c> and <c>DetachView</c> attach
    /// and detach it, so fact, part and view-attached hooks run as in the game; only the view's side of attaching
    /// (<c>EntityViewBase.AttachToData</c>), which sets up models, is replaced: the agent is wired as its Init would wire
    /// it on a GameObject (a SeekerWithClearance with the unit's clearance, a RaycastModifier and two FunnelModifiers),
    /// and the entity-bounds sphere gets a transform of its own, which the game's EntityBoundsController moves. Path
    /// search, avoidance and walking are the game's code; the view's transform is the unit's own position. Visual calls
    /// on the missing model are adapted by the patches in this file, each counted as a boundary.
    /// </summary>
    internal static class OfflineUnitView
    {
        private static readonly FieldInfo CachedPointer = AccessTools.Field(typeof(UnityEngine.Object), "m_CachedPtr");
        private static readonly MethodInfo SetData = AccessTools.PropertySetter(typeof(EntityViewBase), nameof(EntityViewBase.Data));
        private static readonly MethodInfo SetAgent = AccessTools.PropertySetter(typeof(UnitEntityView), nameof(UnitEntityView.AgentASP));
        private static readonly MethodInfo SetAgentUnit = AccessTools.PropertySetter(typeof(UnitMovementAgentBase), nameof(UnitMovementAgentBase.Unit));
        private static readonly FieldInfo StartGetUpTime = AccessTools.Field(typeof(UnitEntityView), "m_StartGetUpTime");
        private static readonly MethodInfo SetTransform = AccessTools.PropertySetter(typeof(EntityViewBase), nameof(EntityViewBase.Transform));
        private static readonly FieldInfo AgentSeeker = AccessTools.Field(typeof(UnitMovementAgent), "m_Seeker");
        private static readonly FieldInfo AgentSmoothModifier = AccessTools.Field(typeof(UnitMovementAgent), "m_SmoothModifier");
        private static readonly FieldInfo AgentFunnelModifier = AccessTools.Field(typeof(UnitMovementAgent), "m_FunnelModifier");
        private static readonly FieldInfo AgentDebugName = AccessTools.Field(typeof(UnitMovementAgent), "m_DebugPathName");
        private static readonly MethodInfo InitMoveDirection = AccessTools.Method(typeof(UnitMovementAgent), "InitMoveDirection");
        private static readonly FieldInfo OverrideSizeScaleField = AccessTools.Field(typeof(UnitEntityView), "m_OverrideSizeScale");
        private static readonly FieldInfo CorpulenceField = AccessTools.Field(typeof(UnitEntityView), "m_Corpulence");
        private static readonly FieldInfo RacialPetScaleCoeff = AccessTools.Field(typeof(UnitEntityView), "m_RacialPetScaleCoeff");
        private static readonly List<UnitMovementAgentBase> AllAgents =
            (List<UnitMovementAgentBase>)AccessTools.Field(typeof(UnitMovementAgentBase), "AllAgents").GetValue(null);

        /// <summary>The objects that make up one stand-in view: what its GameObject and the bound sphere's GameObject hold.</summary>
        private sealed class StandIn
        {
            public UnitViewPrefab Prefab;
            public UnityAssetMapper Mapper;
            public GameObject GameObject;
            public Transform Transform;
            public GameObject BoundsObject;
            public Kingmaker.Controllers.Optimization.EntityDataLink Link;
            public readonly List<UnityEngine.Object> Parts = new();
        }

        private static readonly Dictionary<object, StandIn> StandIns = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, UnitEntityData> Transforms = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, Vector3> StoredPositions = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, Transform> ComponentTransforms = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, GameObject> GameObjects = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, bool> Enabled = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, bool> Active = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, UnitEntityData> BoundsColliders = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, float> Radii = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, List<Vector3>> LinePositions = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, StandIn> Detached = new(ReferenceComparer.Instance);

        // The game's Unity reads an object as live when its native pointer is not zero; == and Equals then compare
        // references, so every stand-in stays a distinct object. GetInstanceID would read behind the pointer at an offset
        // only native code knows; asking for it fails offline as unity-runtime-required, and the game's battle code never
        // asks (only its sound, effect and debug code do). The block is zeroed and never written.
        private static readonly IntPtr NativeStandIn = AllocateZeroed(4096);
        private static readonly MethodInfo AgentOnEnable = AccessTools.Method(typeof(UnitMovementAgent), "OnEnable");
        private static readonly MethodInfo AgentOnDisable = AccessTools.Method(typeof(UnitMovementAgent), "OnDisable");

        internal static int Count => StandIns.Count;

        internal static bool IsStandIn(EntityViewBase view) => view is not null && StandIns.ContainsKey(view);

        internal static void MarkLive(UnityEngine.Object value) => CachedPointer.SetValue(value, NativeStandIn);

        internal static void MarkDead(UnityEngine.Object value) => CachedPointer.SetValue(value, IntPtr.Zero);

        /// <summary>What UnitEntityData.AttachToViewOnLoad(null) does for a new unit: it creates the view and attaches it.</summary>
        internal static void Attach(UnitEntityData unit)
        {
            if (unit.View is not null) throw new InvalidOperationException($"{unit} already has a view.");
            unit.AttachView(CreateView(unit));
        }

        /// <summary>
        /// UnitEntityData.CreateViewForData: CreateView instantiates the prefab (an active polymorph's first, which turns
        /// size scaling off and becomes the polymorph's view replacement), then the view gets the unit's blueprint.
        /// </summary>
        internal static UnitEntityView CreateView(UnitEntityData unit)
        {
            UnitEntityView view;
            var polymorph = unit.GetActivePolymorph();
            if (polymorph.Component is { } component && PolymorphPrefab(unit, component) is { } replacement)
            {
                view = Instantiate(replacement);
                polymorph.GetData<Kingmaker.UnitLogic.Buffs.PolymorphData>().ViewReplacement = StandIns[view].GameObject;
                view.DisableSizeScaling = true;
            }
            else
            {
                view = Instantiate(UnitViewPrefab.For(unit));
            }
            view.Blueprint = unit.Blueprint;
            return view;
        }

        /// <summary>Polymorph.CreatePolymorphView's prefab (<see cref="UnitViewPrefab.ForPolymorph"/>).</summary>
        internal static UnitViewPrefab PolymorphPrefab(UnitEntityData unit, Kingmaker.UnitLogic.Buffs.Polymorph polymorph)
            => UnitViewPrefab.ForPolymorph(unit, polymorph);

        /// <summary>
        /// Unity's Instantiate of a view prefab: a new view, not attached to a unit, with the serialized fields of the
        /// prefab's UnitEntityView, OverrideSizeScale and UnitMovementAgent, as Awake finds them on its GameObject. A
        /// configured corpulence replaces the prefab's; without a bundle, the view and its movement agent keep their class
        /// defaults except the corpulence.
        /// </summary>
        internal static UnitEntityView Instantiate(UnitViewPrefab prefab)
        {
            var standIn = new StandIn { Prefab = prefab, Mapper = UnityAssetMapper.Detached() };
            standIn.GameObject = CreateGameObject(standIn);
            var view = (UnitEntityView)Create(UnitViewPrefab.ViewType(prefab.ViewClass), standIn, standIn.GameObject, null);
            if (prefab.View is { } viewField) CopySerialized(standIn, viewField, view);
            if (prefab.ReplacesCorpulence) CorpulenceField.SetValue(view, prefab.SerializedCorpulence);
            standIn.Transform = (Transform)Create(typeof(Transform), standIn, standIn.GameObject, null);
            ComponentTransforms[view] = standIn.Transform;
            SetTransform.Invoke(view, new object[] { standIn.Transform });
            if (prefab.SizeOverride is { } sizeOverride)
            {
                var component = Create(typeof(OverrideSizeScale), standIn, standIn.GameObject, standIn.Transform);
                CopySerialized(standIn, sizeOverride, component);
                OverrideSizeScaleField.SetValue(view, component);
            }
            if (prefab.MovementAgent != null || prefab.View == null)
            {
                var agent = (UnitMovementAgent)Create(typeof(UnitMovementAgent), standIn, standIn.GameObject, standIn.Transform);
                if (prefab.MovementAgent is { } field) CopySerialized(standIn, field, agent);
                SetAgent.Invoke(view, new object[] { agent });
            }
            StandIns[view] = standIn;
            return view;
        }

        /// <summary>A prefab component's serialized fields, including its enabled flag, copied onto the stand-in's component.</summary>
        private static void CopySerialized(StandIn standIn, AssetsTools.NET.AssetTypeValueField field, UnityEngine.Object component)
        {
            standIn.Mapper.MapInto(field, component);
            if (field["m_Enabled"] is { IsDummy: false } enabled) Enabled[component] = enabled.AsBool;
        }

        /// <summary>
        /// EntityViewBase.AttachToData for a stand-in, called from the game's AttachView: what it and
        /// UnitEntityView.OnDidAttachToData set up that the unit's mechanics read, without models. The view's transform
        /// becomes the unit's position, the size scale is computed afresh (with the mount scale for pets), the movement
        /// agent is initialized, and the bound sphere links to the unit (SetupSphereColliderEntityLink).
        /// </summary>
        internal static bool TryAttachToData(EntityViewBase entityView, EntityDataBase data)
        {
            if (!StandIns.TryGetValue(entityView, out var standIn)) return false;
            var view = (UnitEntityView)entityView;
            if (view.Data == data) return true;
            if (view.Data != null) view.DetachFromData();
            var unit = (UnitEntityData)data;
            SetData.Invoke(view, new object[] { unit });
            view.UniqueId = unit.UniqueId;
            Transforms[standIn.Transform] = unit;
            view.Blueprint = unit.Blueprint;
            RacialPetScaleCoeff.SetValue(view, unit.Master == null
                ? 1f
                : Kingmaker.Game.Instance.BlueprintRoot.MountModelSizing.GetCoeff(unit.Master.Progression.Race?.RaceId ?? Kingmaker.Blueprints.Race.Human));
            view.ResetSizeScale();
            // IsGetUp counts a unit as standing up for 0.5 s after this time. In the game, game time is far from zero, so a
            // unit that never fell is not standing up; offline game time starts at zero, so the time lies a day earlier.
            StartGetUpTime.SetValue(view, Kingmaker.Game.Instance.Player.GameTime - TimeSpan.FromDays(1));
            if (view.AgentASP is UnitMovementAgent agent && agent.Unit is null) InitMovementAgent(view, agent, standIn);
            // EntityBoundsController gives every unit a sphere collider on a GameObject of its own in the entity-bounds
            // physics scene, with the unit's corpulence as radius; vision range and area queries overlap these spheres.
            if (view.SphereBoundsCollider is null)
            {
                standIn.BoundsObject = CreateGameObject(standIn);
                var boundsTransform = (Transform)Create(typeof(Transform), standIn, standIn.BoundsObject, null);
                StoredPositions[boundsTransform] = unit.Position;
                var bounds = (SphereCollider)Create(typeof(SphereCollider), standIn, standIn.BoundsObject, boundsTransform);
                Radii[bounds] = Math.Max(0.05f, view.Corpulence);
                BoundsColliders[bounds] = unit;
                view.SphereBoundsCollider = bounds;
                standIn.Link = (Kingmaker.Controllers.Optimization.EntityDataLink)Create(
                    typeof(Kingmaker.Controllers.Optimization.EntityDataLink), standIn, standIn.BoundsObject, boundsTransform);
            }
            BoundsColliders[view.SphereBoundsCollider] = unit;
            standIn.Link.Entity = unit;
            Boundaries.Hit("unit-view-stand-in");
            BattleWorker.NoteUnitView(standIn.Prefab, standIn.Mapper.Gaps);
            return true;
        }

        /// <summary>
        /// EntityViewBase.DetachFromData for a stand-in, called from the game's DetachView: the bound sphere no longer
        /// links to the unit, and UnitEntityView.OnWillDetachFromData resets the size scale; the hands hold no models.
        /// </summary>
        internal static bool TryDetachFromData(EntityViewBase entityView)
        {
            if (!StandIns.TryGetValue(entityView, out var standIn)) return false;
            var view = (UnitEntityView)entityView;
            if (view.Data == null) return true;
            if (standIn.Link != null) standIn.Link.Entity = null;
            if (view.SphereBoundsCollider is { } bounds) BoundsColliders.Remove(bounds);
            view.ResetSizeScale();
            Transforms.Remove(standIn.Transform);
            SetData.Invoke(view, new object[] { null });
            return true;
        }

        /// <summary>What UnitMovementAgent.Init and OnEnable set up on the unit's GameObject, without the GameObject.</summary>
        private static void InitMovementAgent(UnitEntityView view, UnitMovementAgent agent, StandIn standIn)
        {
            SetAgentUnit.Invoke(agent, new object[] { view });
            var seeker = (Kingmaker.TurnBasedMode.Pathfinding.SeekerWithClearance)Create(
                typeof(Kingmaker.TurnBasedMode.Pathfinding.SeekerWithClearance), standIn, standIn.GameObject, standIn.Transform);
            seeker.startEndModifier.Awake(seeker);
            seeker.clearance = new Vector3(view.Corpulence * 2f, 0f, view.Corpulence * 2f);
            var smooth = Modifier<Pathfinding.RaycastModifier>(seeker, standIn);
            smooth.useRaycasting = false;
            smooth.useGraphRaycasting = true;
            var funnel = Modifier<Pathfinding.FunnelModifier>(seeker, standIn);
            Modifier<Pathfinding.FunnelModifier>(seeker, standIn);
            seeker.startEndModifier.exactStartPoint = Pathfinding.StartEndModifier.Exactness.Original;
            AgentSeeker.SetValue(agent, seeker);
            AgentSmoothModifier.SetValue(agent, smooth);
            AgentFunnelModifier.SetValue(agent, funnel);
            InitMoveDirection.Invoke(agent, null);
            AgentDebugName.SetValue(agent, view.UniqueId);
            if (IsActiveAndEnabled(agent)) AllAgents.Add(agent);
        }

        private static T Modifier<T>(Pathfinding.Seeker seeker, StandIn standIn) where T : Pathfinding.MonoModifier
        {
            var modifier = (T)Create(typeof(T), standIn, standIn.GameObject, standIn.Transform);
            modifier.seeker = seeker;
            seeker.RegisterModifier(modifier);
            return modifier;
        }

        private static GameObject CreateGameObject(StandIn standIn)
        {
            // GameObject's constructor creates the native object, so the stand-in is made without it.
            var gameObject = (GameObject)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(GameObject));
            MarkLive(gameObject);
            standIn.Parts.Add(gameObject);
            Active[gameObject] = true;
            return gameObject;
        }

        private static UnityEngine.Object Create(Type type, StandIn standIn, GameObject gameObject, Transform transform)
        {
            var value = (UnityEngine.Object)Activator.CreateInstance(type, nonPublic: true);
            MarkLive(value);
            standIn.Parts.Add(value);
            GameObjects[value] = gameObject;
            if (transform != null) ComponentTransforms[value] = transform;
            if (value is Behaviour or Collider or Renderer) Enabled[value] = true;
            return value;
        }

        /// <summary>
        /// Removes the unit's stand-in with the game's own DetachView, then destroys it, so the game destroys and disposes
        /// the unit as a unit without a view.
        /// </summary>
        internal static void Detach(UnitEntityData unit)
        {
            if (unit.View is not UnitEntityView view || !StandIns.ContainsKey(view)) return;
            unit.DetachView();
            Destroy(view);
        }

        /// <summary>
        /// After the game disposed a unit: the game destroys a unit's view object when it disposes the unit
        /// (<c>AlwaysDestroyViewOnDispose</c>), which for a stand-in is <see cref="Destroy"/>. A stand-in a failed disposal
        /// left behind is detached and destroyed here.
        /// </summary>
        internal static void Release(UnitEntityData unit, UnitEntityView view)
        {
            if (view is null || !StandIns.ContainsKey(view)) return;
            if (ReferenceEquals(unit.View, view)) unit.DetachView();
            Destroy(view);
        }

        /// <summary>
        /// Object.Destroy of a stand-in's GameObjects: the agent leaves the agents list and its obstacle group as its
        /// OnDisable does, its claimed path is released, and every part reads as destroyed.
        /// </summary>
        internal static void Destroy(UnitEntityView view)
        {
            if (!StandIns.TryGetValue(view, out var standIn)) return;
            if (view.AgentASP is { } agent)
            {
                AllAgents.Remove(agent);
                ObstaclesHelper.RemoveFromGroup(agent);
                (AgentSeeker.GetValue(agent) as Pathfinding.Seeker)?.ReleaseClaimedPath();
            }
            if (standIn.Link != null) standIn.Link.Entity = null;
            ReleaseParts(standIn);
            StandIns.Remove(view);
        }

        private static void ReleaseParts(StandIn standIn)
        {
            foreach (var part in standIn.Parts)
            {
                BoundsColliders.Remove(part);
                Radii.Remove(part);
                Transforms.Remove(part);
                StoredPositions.Remove(part);
                ComponentTransforms.Remove(part);
                GameObjects.Remove(part);
                Enabled.Remove(part);
                Active.Remove(part);
                LinePositions.Remove(part);
                MarkDead(part);
            }
        }

        /// <summary>
        /// A presentation component the game's code expects on a GameObject of its own (a line renderer, a decal), as a
        /// stand-in without a native object: the GameObject keeps an active flag, the component an enabled flag, and its
        /// transform keeps the position it is given. Released with <see cref="ReleaseDetached"/>.
        /// </summary>
        internal static Component CreateDetached(Type type)
        {
            var standIn = new StandIn();
            standIn.GameObject = CreateGameObject(standIn);
            standIn.Transform = (Transform)Create(typeof(Transform), standIn, standIn.GameObject, null);
            StoredPositions[standIn.Transform] = Vector3.zero;
            var component = (Component)Create(type, standIn, standIn.GameObject, standIn.Transform);
            if (component is LineRenderer) LinePositions[component] = new List<Vector3>();
            Detached[component] = standIn;
            return component;
        }

        internal static void ReleaseDetached(Component component)
        {
            if (component is null || !Detached.TryGetValue(component, out var standIn)) return;
            ReleaseParts(standIn);
            Detached.Remove(component);
        }

        internal static int DetachedCount => Detached.Count;

        /// <summary>
        /// The components on a stand-in GameObject, its transform first as in Unity, then in the order they were added;
        /// null for any other GameObject.
        /// </summary>
        internal static List<Component> ComponentsOn(GameObject gameObject)
        {
            if (gameObject is null || !Active.ContainsKey(gameObject)) return null;
            return GameObjects.Where(pair => ReferenceEquals(pair.Value, gameObject)).Select(pair => pair.Key).OfType<Component>()
                .OrderBy(component => component is Transform ? 0 : 1).ToList();
        }

        /// <summary>The points of a stand-in line renderer, or null for any other line renderer.</summary>
        internal static List<Vector3> LinePoints(LineRenderer line)
            => line is not null && LinePositions.TryGetValue(line, out var points) ? points : null;

        /// <summary>The stand-in GameObject a stand-in component sits on, or null for any other component.</summary>
        internal static GameObject ComponentGameObject(Component component)
            => component is not null && GameObjects.TryGetValue(component, out var gameObject) ? gameObject : null;

        /// <summary>The unit whose stand-in view owns this transform, or null for any other transform.</summary>
        internal static UnitEntityData TransformOwner(Transform transform)
            => transform is not null && Transforms.TryGetValue(transform, out var unit) ? unit : null;

        /// <summary>
        /// A stand-in transform that keeps the position it is given, as the entity-bounds sphere's GameObject does: the
        /// game's EntityBoundsController moves it to the unit once per frame. Its scale is one.
        /// </summary>
        internal static bool TryGetStoredPosition(Transform transform, out Vector3 position)
        {
            position = default;
            return transform is not null && StoredPositions.TryGetValue(transform, out position);
        }

        internal static bool TrySetStoredPosition(Transform transform, Vector3 position)
        {
            if (transform is null || !StoredPositions.ContainsKey(transform)) return false;
            StoredPositions[transform] = position;
            return true;
        }

        internal static Transform ComponentTransform(Component component)
            => component is not null && ComponentTransforms.TryGetValue(component, out var transform) ? transform : null;

        internal static bool TryGetEnabled(UnityEngine.Object component, out bool enabled) => Enabled.TryGetValue(component, out enabled);

        internal static bool TryGetRadius(SphereCollider collider, out float radius) => Radii.TryGetValue(collider, out radius);

        internal static bool TrySetRadius(SphereCollider collider, float radius)
        {
            if (!Radii.ContainsKey(collider)) return false;
            Radii[collider] = radius;
            return true;
        }

        /// <summary>Unit bound spheres that overlap the capsule from a to b (a sphere when a equals b).</summary>
        internal static int OverlapUnitBounds(Vector3 a, Vector3 b, float radius, Collider[] results)
        {
            var count = 0;
            foreach (var pair in BoundsColliders)
            {
                if (count >= results.Length) break;
                var center = StoredPositions[ComponentTransforms[pair.Key]];
                var segment = b - a;
                var along = segment.sqrMagnitude < 1e-12f ? 0f : Mathf.Clamp01(Vector3.Dot(center - a, segment) / segment.sqrMagnitude);
                var closest = a + segment * along;
                // Unity's physics leaves out disabled colliders and colliders on inactive GameObjects.
                if (!IsActiveAndEnabled(pair.Key)) continue;
                if ((center - closest).magnitude <= radius + Radii[pair.Key]) results[count++] = (Collider)pair.Key;
            }
            return count;
        }

        /// <summary>
        /// A stand-in component's enabled flag. Unity runs OnEnable or OnDisable when the flag of a component on an active
        /// GameObject changes; for the movement agent, once it is set up, the game's own run, which add it to or remove it
        /// from the agents list and its obstacle group. The other stand-in components only hold the flag.
        /// </summary>
        internal static bool TrySetEnabled(UnityEngine.Object component, bool enabled)
        {
            if (!Enabled.TryGetValue(component, out var current)) return false;
            Enabled[component] = enabled;
            if (current != enabled && ActiveSelf(component)) NotifyEnabled(component, enabled);
            return true;
        }

        internal static bool TryGetActive(GameObject gameObject, out bool active) => Active.TryGetValue(gameObject, out active);

        /// <summary>
        /// GameObject.SetActive of a stand-in GameObject. As in Unity, the enabled components on it get OnDisable when it
        /// becomes inactive and OnEnable when it becomes active again.
        /// </summary>
        internal static bool TrySetActive(GameObject gameObject, bool active)
        {
            if (!Active.TryGetValue(gameObject, out var current)) return false;
            Active[gameObject] = active;
            if (current == active) return true;
            foreach (var pair in GameObjects.Where(pair => ReferenceEquals(pair.Value, gameObject)).ToList())
                if (Enabled.TryGetValue(pair.Key, out var enabled) && enabled) NotifyEnabled((UnityEngine.Object)pair.Key, active);
            return true;
        }

        private static bool ActiveSelf(object component)
            => !GameObjects.TryGetValue(component, out var gameObject) || !Active.TryGetValue(gameObject, out var active) || active;

        private static bool IsActiveAndEnabled(object component)
            => (!Enabled.TryGetValue(component, out var enabled) || enabled) && ActiveSelf(component);

        private static void NotifyEnabled(UnityEngine.Object component, bool enabled)
        {
            if (component is UnitMovementAgent { Unit: not null } agent) (enabled ? AgentOnEnable : AgentOnDisable).Invoke(agent, null);
        }

        private static IntPtr AllocateZeroed(int size)
        {
            var block = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, block, size);
            return block;
        }
    }

    /// <summary>
    /// Components of a stand-in share its transform and GameObject; their enabled flag lives here, as no native object
    /// holds it.
    /// </summary>
    [HarmonyPatch]
    internal static class StandInComponentPatches
    {
        [HarmonyPatch(typeof(Component), "get_gameObject"), HarmonyPrefix]
        private static bool GetGameObject(Component __instance, ref GameObject __result)
        {
            if (OfflineUnitView.ComponentGameObject(__instance) is not { } gameObject) return true;
            __result = gameObject;
            return false;
        }

        [HarmonyPatch(typeof(Component), "get_transform"), HarmonyPrefix]
        private static bool GetTransform(Component __instance, ref Transform __result)
        {
            if (OfflineUnitView.ComponentTransform(__instance) is not { } transform) return true;
            __result = transform;
            return false;
        }

        [HarmonyPatch(typeof(Behaviour), "get_enabled"), HarmonyPrefix]
        private static bool GetEnabled(Behaviour __instance, ref bool __result)
            => !OfflineUnitView.TryGetEnabled(__instance, out __result);

        [HarmonyPatch(typeof(Behaviour), "set_enabled"), HarmonyPrefix]
        private static bool SetEnabled(Behaviour __instance, bool value) => !OfflineUnitView.TrySetEnabled(__instance, value);

        [HarmonyPatch(typeof(Collider), "get_enabled"), HarmonyPrefix]
        private static bool GetColliderEnabled(Collider __instance, ref bool __result)
            => !OfflineUnitView.TryGetEnabled(__instance, out __result);

        [HarmonyPatch(typeof(Collider), "set_enabled"), HarmonyPrefix]
        private static bool SetColliderEnabled(Collider __instance, bool value) => !OfflineUnitView.TrySetEnabled(__instance, value);

        [HarmonyPatch(typeof(Renderer), "get_enabled"), HarmonyPrefix]
        private static bool GetRendererEnabled(Renderer __instance, ref bool __result)
            => !OfflineUnitView.TryGetEnabled(__instance, out __result);

        [HarmonyPatch(typeof(Renderer), "set_enabled"), HarmonyPrefix]
        private static bool SetRendererEnabled(Renderer __instance, bool value) => !OfflineUnitView.TrySetEnabled(__instance, value);

        [HarmonyPatch(typeof(GameObject), "get_activeSelf"), HarmonyPrefix]
        private static bool GetActiveSelf(GameObject __instance, ref bool __result) => !OfflineUnitView.TryGetActive(__instance, out __result);

        // A stand-in GameObject has no parent, so it is active in the hierarchy exactly when it is active itself.
        [HarmonyPatch(typeof(GameObject), "get_activeInHierarchy"), HarmonyPrefix]
        private static bool GetActiveInHierarchy(GameObject __instance, ref bool __result) => !OfflineUnitView.TryGetActive(__instance, out __result);

        [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive)), HarmonyPrefix]
        private static bool SetActive(GameObject __instance, bool value) => !OfflineUnitView.TrySetActive(__instance, value);
    }

    /// <summary>
    /// Unity's component lookups on stand-in GameObjects (GetComponent, TryGetComponent, GetComponents, and a GameObject's
    /// transform), answered from the stand-in's own components as Unity answers from the native object: the first
    /// component of the type, or every one. Unity's generic GetComponent passes the address one pointer past the field
    /// that receives the result; the result is stored there, as the native call does. Any other GameObject runs the
    /// native call.
    /// </summary>
    [HarmonyPatch]
    internal static class StandInLookupPatches
    {
        private static readonly Action<IntPtr, object> StoreReference = CreateStore();

        private static Action<IntPtr, object> CreateStore()
        {
            var method = new System.Reflection.Emit.DynamicMethod("StoreReference", typeof(void), new[] { typeof(IntPtr), typeof(object) },
                typeof(StandInLookupPatches).Module, skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Stind_Ref);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (Action<IntPtr, object>)method.CreateDelegate(typeof(Action<IntPtr, object>));
        }

        private static Component First(List<Component> components, Type type)
        {
            Boundaries.Hit("unit-view-stand-in");
            return components.FirstOrDefault(type.IsInstanceOfType);
        }

        private static bool Store(List<Component> components, Type type, IntPtr oneFurtherThanResultValue)
        {
            if (components == null) return true;
            StoreReference(oneFurtherThanResultValue - IntPtr.Size, First(components, type));
            return false;
        }

        [HarmonyPatch(typeof(GameObject), "GetComponentFastPath"), HarmonyPrefix]
        private static bool GetComponentFastPath(GameObject __instance, Type type, IntPtr oneFurtherThanResultValue)
            => Store(OfflineUnitView.ComponentsOn(__instance), type, oneFurtherThanResultValue);

        [HarmonyPatch(typeof(GameObject), "TryGetComponentFastPath"), HarmonyPrefix]
        private static bool TryGetComponentFastPath(GameObject __instance, Type type, IntPtr oneFurtherThanResultValue)
            => Store(OfflineUnitView.ComponentsOn(__instance), type, oneFurtherThanResultValue);

        [HarmonyPatch(typeof(Component), "GetComponentFastPath"), HarmonyPrefix]
        private static bool ComponentGetComponentFastPath(Component __instance, Type type, IntPtr oneFurtherThanResultValue)
            => Store(OfflineUnitView.ComponentsOn(OfflineUnitView.ComponentGameObject(__instance)), type, oneFurtherThanResultValue);

        [HarmonyPatch(typeof(GameObject), nameof(GameObject.GetComponent), typeof(Type)), HarmonyPrefix]
        private static bool GetComponent(GameObject __instance, Type type, ref Component __result)
        {
            if (OfflineUnitView.ComponentsOn(__instance) is not { } components) return true;
            __result = First(components, type);
            return false;
        }

        [HarmonyPatch(typeof(GameObject), "TryGetComponentInternal"), HarmonyPrefix]
        private static bool TryGetComponentInternal(GameObject __instance, Type type, ref Component __result)
        {
            if (OfflineUnitView.ComponentsOn(__instance) is not { } components) return true;
            __result = First(components, type);
            return false;
        }

        // A stand-in GameObject has no children, so the recursive searches find what the GameObject itself holds.
        [HarmonyPatch(typeof(GameObject), "GetComponentsInternal"), HarmonyPrefix]
        private static bool GetComponentsInternal(GameObject __instance, Type type, bool useSearchTypeAsArrayReturnType, object resultList, ref Array __result)
        {
            if (OfflineUnitView.ComponentsOn(__instance) is not { } components) return true;
            Boundaries.Hit("unit-view-stand-in");
            __result = Fill(components.Where(type.IsInstanceOfType).ToList(), useSearchTypeAsArrayReturnType ? type : typeof(Component), resultList);
            return false;
        }

        [HarmonyPatch(typeof(Component), "GetComponentsForListInternal"), HarmonyPrefix]
        private static bool GetComponentsForList(Component __instance, Type searchType, object resultList)
        {
            if (OfflineUnitView.ComponentsOn(OfflineUnitView.ComponentGameObject(__instance)) is not { } components) return true;
            Boundaries.Hit("unit-view-stand-in");
            Fill(components.Where(searchType.IsInstanceOfType).ToList(), searchType, resultList);
            return false;
        }

        [HarmonyPatch(typeof(GameObject), "get_transform"), HarmonyPrefix]
        private static bool GetTransform(GameObject __instance, ref Transform __result)
        {
            if (OfflineUnitView.ComponentsOn(__instance) is not { } components) return true;
            __result = (Transform)First(components, typeof(Transform));
            return false;
        }

        private static Array Fill(List<Component> matches, Type elementType, object resultList)
        {
            if (resultList is System.Collections.IList list)
            {
                list.Clear();
                foreach (var match in matches) list.Add(match);
                return null;
            }
            var array = Array.CreateInstance(elementType, matches.Count);
            for (var i = 0; i < matches.Count; i++) array.SetValue(matches[i], i);
            return array;
        }
    }

    /// <summary>
    /// The view's side of the game's AttachView and DetachView. For a stand-in it sets up and releases what the unit's
    /// mechanics read (<see cref="OfflineUnitView.TryAttachToData"/>); for any other view the game's code runs.
    /// </summary>
    [HarmonyPatch(typeof(EntityViewBase))]
    internal static class StandInAttachPatches
    {
        [HarmonyPatch(nameof(EntityViewBase.AttachToData)), HarmonyPrefix]
        private static bool AttachToData(EntityViewBase __instance, EntityDataBase data) => !OfflineUnitView.TryAttachToData(__instance, data);

        [HarmonyPatch(nameof(EntityViewBase.DetachFromData)), HarmonyPrefix]
        private static bool DetachFromData(EntityViewBase __instance) => !OfflineUnitView.TryDetachFromData(__instance);
    }

    /// <summary>
    /// A polymorph replaces the unit's view with a view of its own prefab (Polymorph.TryReplaceView) and brings the unit's
    /// view back when it ends (RestoreView). For a stand-in, the new view is a stand-in made from that prefab and attached
    /// with the game's AttachToViewOnLoad and AttachView, and the methods' other effects run through the game's API as in
    /// the game: size scaling off for the polymorph's view, the view replacement record, interrupting every command but
    /// movement, and waking the unit on restore. Models, transforms, selection marks and the visual fade between the two
    /// views are skipped; the old view is destroyed at once.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Buffs.Polymorph))]
    internal static class PolymorphViewPatches
    {
        private static readonly PropertyInfo Owner = AccessTools.Property(typeof(Kingmaker.UnitLogic.Buffs.Polymorph), "Owner");
        private static readonly PropertyInfo Data = AccessTools.Property(typeof(Kingmaker.UnitLogic.Buffs.Polymorph), "Data");
        private static readonly PropertyInfo HasExternalTransition = AccessTools.Property(typeof(Kingmaker.UnitLogic.Buffs.Polymorph), "HasExternalTransition");
        private static readonly FieldInfo TransitionExternal = AccessTools.Field(typeof(Kingmaker.UnitLogic.Buffs.Polymorph), "m_TransitionExternal");
        private static readonly FieldInfo ExitTransition = AccessTools.Field(typeof(Kingmaker.UnitLogic.Buffs.Polymorph), "m_ExitTransition");

        [HarmonyPatch("TryReplaceView"), HarmonyPrefix]
        private static bool TryReplaceView(Kingmaker.UnitLogic.Buffs.Polymorph __instance)
        {
            var owner = (UnitEntityData)Owner.GetValue(__instance);
            if (!OfflineUnitView.IsStandIn(owner.View)) return true;
            var data = (Kingmaker.UnitLogic.Buffs.PolymorphData)Data.GetValue(__instance);
            if ((bool)data.ViewReplacement || !owner.View) return false;
            if (OfflineUnitView.PolymorphPrefab(owner, __instance) is not { } prefab) return false;
            foreach (var buff in owner.Buffs) buff.ClearParticleEffect();
            var previous = owner.View;
            var view = OfflineUnitView.Instantiate(prefab);
            view.UniqueId = owner.UniqueId;
            view.DisableSizeScaling = true;
            view.Blueprint = owner.Blueprint;
            owner.AttachToViewOnLoad(view);
            data.ViewReplacement = OfflineUnitView.ComponentGameObject(view);
            owner.Commands.InterruptAll(command => command is not Kingmaker.UnitLogic.Commands.UnitMoveTo);
            OfflineUnitView.Destroy(previous);
            Boundaries.Hit("unit-view-stand-in");
            return false;
        }

        [HarmonyPatch("RestoreView"), HarmonyPrefix]
        private static bool RestoreView(Kingmaker.UnitLogic.Buffs.Polymorph __instance)
        {
            var owner = (UnitEntityData)Owner.GetValue(__instance);
            if (!OfflineUnitView.IsStandIn(owner.View)) return true;
            var data = (Kingmaker.UnitLogic.Buffs.PolymorphData)Data.GetValue(__instance);
            if (!data.ViewReplacement) return false;
            foreach (var buff in owner.Buffs) buff.ClearParticleEffect();
            var previous = owner.View;
            // AttachToViewOnLoad(null): the polymorph is ending, so CreateViewForData makes the unit's own view again.
            owner.AttachView(OfflineUnitView.CreateView(owner));
            owner.Commands.InterruptAll(command => command is not Kingmaker.UnitLogic.Commands.UnitMoveTo);
            OfflineUnitView.Destroy(previous);
            var settings = (bool)HasExternalTransition.GetValue(__instance)
                ? ((Kingmaker.Visual.PolymorphTransitionSettings)TransitionExternal.GetValue(__instance)).ExitTransition
                : (Kingmaker.UnitLogic.Buffs.Polymorph.VisualTransitionSettings)ExitTransition.GetValue(__instance);
            owner.Wake(settings.ScaleTime);
            data.ViewReplacement = null;
            Boundaries.Hit("unit-view-stand-in");
            return false;
        }
    }

    [HarmonyPatch(typeof(UnitEntityView))]
    internal static class UnitViewPatches
    {
        /// <summary>Places the UI's destination marker when a directly controllable unit starts a path; no UI exists offline.</summary>
        [HarmonyPatch("OnMovementStarted"), HarmonyPrefix]
        private static bool OnMovementStarted(UnitEntityView __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            Boundaries.Hit("move-target-marker");
            return false;
        }

        /// <summary>Draws the weapon models in the unit's hands and toggles occlusion clipping; the stand-in has no model.</summary>
        [HarmonyPatch(nameof(UnitEntityView.HandleAttackCommandRun)), HarmonyPrefix]
        private static bool HandleAttackCommandRun(UnitEntityView __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            Boundaries.Hit("unit-weapon-visuals");
            return false;
        }

        /// <summary>Object.Destroy of the view's GameObject, which the game calls when it disposes a unit: the stand-in is destroyed.</summary>
        [HarmonyPatch(typeof(EntityViewBase), nameof(EntityViewBase.DestroyViewObject)), HarmonyPrefix]
        private static bool DestroyViewObject(EntityViewBase __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            OfflineUnitView.Destroy((UnitEntityView)__instance);
            Boundaries.Hit("unit-view-stand-in");
            return false;
        }

        /// <summary>Hides the off-hand weapon model while an ability is used; the stand-in has no models.</summary>
        [HarmonyPatch(nameof(UnitEntityView.HideOffWeapon)), HarmonyPrefix]
        private static bool HideOffWeapon(UnitEntityView __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            Boundaries.Hit("unit-weapon-visuals");
            return false;
        }

        /// <summary>The highlight of a unit under the mouse, in a drag box or among the objects the player can use; no UI exists offline.</summary>
        [HarmonyPatch(nameof(UnitEntityView.UpdateHighlight)), HarmonyPrefix]
        private static bool UpdateHighlight(UnitEntityView __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            Boundaries.Hit("unit-highlight");
            return false;
        }

        /// <summary>
        /// The view's side of a death: the blood and dismemberment effect, the fade, occlusion clipping, a swarm's health
        /// display, death effects of the prefab and the click collider that follows the torso. The stand-in has none of these.
        /// </summary>
        [HarmonyPatch(nameof(UnitEntityView.HandleDeath)), HarmonyPrefix]
        private static bool HandleDeath(UnitEntityView __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance)) return true;
            Boundaries.Hit("unit-death-visuals");
            return false;
        }
    }

    /// <summary>
    /// Blood, hit effects and sounds of dealt damage. The game returns early when either unit has no live view; a stand-in
    /// has no model to play them on, so it takes the same return.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.HitSystem.HitPlayer), nameof(Kingmaker.Visual.HitSystem.HitPlayer.PlayDamageHit))]
    internal static class DamageHitPatch
    {
        private static bool Prefix(Kingmaker.RuleSystem.Rules.Damage.RuleDealDamage damageRule)
        {
            if (!OfflineUnitView.IsStandIn(damageRule.Target?.View) && !OfflineUnitView.IsStandIn(damageRule.Initiator?.View)) return true;
            Boundaries.Hit("unit-hit-effects");
            return false;
        }
    }

    /// <summary>
    /// The visual and sound side of using an ability (<c>UnitUseAbility.CastAbilityFx</c>): the blueprint's
    /// AbilitySpawnFx effects, the precast effects in the hands and on the ground, and the caster's spell voice. The
    /// ability itself is cast and resolved by the game's code.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Commands.UnitUseAbility), "CastAbilityFx")]
    internal static class AbilityCastFxPatch
    {
        private static bool Prefix(Kingmaker.UnitLogic.Commands.UnitUseAbility __instance)
        {
            if (!OfflineUnitView.IsStandIn(__instance.Executor?.View)) return true;
            Boundaries.Hit("ability-cast-fx");
            return false;
        }
    }

    /// <summary>
    /// The game's UnitHandEquipmentController waits until a unit's hand models match its combat state before the unit
    /// starts a scheduled command. A stand-in has no models, so there is nothing to wait for.
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Controllers.Units.UnitHandEquipmentController), "DidUpdate")]
    internal static class HandEquipmentUpdatePatch
    {
        private static bool Prefix(UnitEntityData unit, ref bool __result)
        {
            if (!OfflineUnitView.IsStandIn(unit?.View)) return true;
            Boundaries.Hit("unit-weapon-visuals");
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Time), "get_frameCount")]
    internal static class FrameCountPatch
    {
        private static bool Prefix(ref int __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("frame-clock");
            __result = BattleClock.FrameCount;
            return false;
        }
    }

    [HarmonyPatch(typeof(Time), "get_realtimeSinceStartup")]
    internal static class RealtimePatch
    {
        private static bool Prefix(ref float __result)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("frame-clock");
            __result = BattleClock.RealTime;
            return false;
        }
    }

    /// <summary>
    /// A stand-in view's Transform reads as the unit: its position is the unit's position and its rotation the unit's
    /// orientation, which the game keeps in UnitEntityData. Writes to either are dropped, because only the model would
    /// show them; game code that moves a unit sets the data as well (UnitPlaceOnGroundController sets both, for example).
    /// The entity-bounds sphere's transform keeps the position the game's EntityBoundsController gives it, with scale one.
    /// </summary>
    [HarmonyPatch(typeof(Transform))]
    internal static class StandInTransformPatches
    {
        [HarmonyPatch("get_position_Injected"), HarmonyPrefix]
        private static bool GetPosition(Transform __instance, ref Vector3 ret)
        {
            if (OfflineUnitView.TryGetStoredPosition(__instance, out ret)) return false;
            if (OfflineUnitView.TransformOwner(__instance) is not { } unit) return true;
            ret = unit.Position;
            return false;
        }

        [HarmonyPatch("set_position_Injected"), HarmonyPrefix]
        private static bool SetPosition(Transform __instance, ref Vector3 value)
            => !OfflineUnitView.TrySetStoredPosition(__instance, value) && OfflineUnitView.TransformOwner(__instance) is null;

        [HarmonyPatch("get_lossyScale_Injected"), HarmonyPrefix]
        private static bool GetLossyScale(Transform __instance, ref Vector3 ret)
        {
            if (!OfflineUnitView.TryGetStoredPosition(__instance, out _)) return true;
            ret = Vector3.one;
            return false;
        }

        [HarmonyPatch("get_rotation_Injected"), HarmonyPrefix]
        private static bool GetRotation(Transform __instance, ref Quaternion ret)
        {
            if (OfflineUnitView.TransformOwner(__instance) is not { } unit) return true;
            ret = UnityMath.FromEulerRad(new Vector3(0f, unit.Orientation * (float)(Math.PI / 180.0), 0f));
            return false;
        }

        [HarmonyPatch("set_rotation_Injected"), HarmonyPrefix]
        private static bool SetRotation(Transform __instance) => OfflineUnitView.TransformOwner(__instance) is null;
    }

    /// <summary>
    /// Unity's clock while a battle runs. Offline Unity has no frame clock, so every battle frame is 1/30 s of real
    /// time: <see cref="BeginFrame"/> advances the frame count, real time and Unity's scaled and unscaled time, with
    /// <c>Time.deltaTime</c> the frame scaled by <c>Time.timeScale</c>, as Unity does at the start of a frame. The game's
    /// own TimeController then runs in the default mode and sets the time scale and game time from these values.
    /// </summary>
    internal static class BattleClock
    {
        internal const float FrameSeconds = 1f / 30f;

        private static readonly MethodInfo SetDelta = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.TimeController), nameof(Kingmaker.Controllers.TimeController.DeltaTime));
        private static readonly MethodInfo SetGameDelta = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.TimeController), nameof(Kingmaker.Controllers.TimeController.GameDeltaTime));

        private static int frameNumber;
        private static double realTime;
        private static double unscaledTime;
        private static double time;
        private static float timeScale = 1f;
        private static float deltaTime = FrameSeconds;

        /// <summary>Unity's real time since startup: the sum of the battle frames' durations, so it is repeatable.</summary>
        internal static float RealTime => (float)realTime;

        /// <summary>True while battle frames run; Time.frameCount then stays the same within a frame.</summary>
        internal static bool InFrames { get; set; }

        /// <summary>
        /// Unity's frame counter while a battle runs its game loop. The game caches values per frame with it (PerFrameVar,
        /// for example the unit's speed). During battle frames it is the frame's number; between them every read is a new
        /// frame, so a cache never returns a value from before a change the test made. Outside battles the native call
        /// runs and fails as before.
        /// </summary>
        internal static int FrameCount => InFrames ? frameNumber : ++frameNumber;

        /// <summary>Unity's Time.time: scaled time at the start of the frame.</summary>
        internal static float Time => (float)time;

        internal static float UnscaledTime => (float)unscaledTime;

        /// <summary>Unity's Time.deltaTime: the frame's duration scaled by the time scale in effect when it began.</summary>
        internal static float DeltaTime => deltaTime;

        internal static float UnscaledDeltaTime => FrameSeconds;

        /// <summary>Unity's Time.timeScale, which the game's TimeController sets every tick. Unity refuses negative values.</summary>
        internal static float TimeScale
        {
            get => timeScale;
            set => timeScale = value < 0f ? timeScale : value;
        }

        /// <summary>The start of a Unity frame: the clock advances before any Update runs.</summary>
        internal static void BeginFrame()
        {
            frameNumber++;
            realTime += FrameSeconds;
            unscaledTime += FrameSeconds;
            deltaTime = FrameSeconds * timeScale;
            time += deltaTime;
            Boundaries.Hit("frame-clock");
        }

        /// <summary>Puts the TimeController's frame durations back, as cleanup does after a battle.</summary>
        internal static void Set(float delta, float gameDelta)
        {
            var controller = Kingmaker.Game.Instance.TimeController;
            SetDelta.Invoke(controller, new object[] { delta });
            SetGameDelta.Invoke(controller, new object[] { gameDelta });
        }
    }

    /// <summary>Unity's Time members while a battle runs its game loop, from <see cref="BattleClock"/>.</summary>
    [HarmonyPatch(typeof(Time))]
    internal static class TimePatches
    {
        [HarmonyPatch("get_time"), HarmonyPrefix]
        private static bool GetTime(ref float __result) => !Answer(BattleClock.Time, ref __result);

        [HarmonyPatch("get_unscaledTime"), HarmonyPrefix]
        private static bool GetUnscaledTime(ref float __result) => !Answer(BattleClock.UnscaledTime, ref __result);

        [HarmonyPatch("get_deltaTime"), HarmonyPrefix]
        private static bool GetDeltaTime(ref float __result) => !Answer(BattleClock.DeltaTime, ref __result);

        [HarmonyPatch("get_unscaledDeltaTime"), HarmonyPrefix]
        private static bool GetUnscaledDeltaTime(ref float __result) => !Answer(BattleClock.UnscaledDeltaTime, ref __result);

        [HarmonyPatch("get_timeScale"), HarmonyPrefix]
        private static bool GetTimeScale(ref float __result) => !Answer(BattleClock.TimeScale, ref __result);

        [HarmonyPatch("set_timeScale"), HarmonyPrefix]
        private static bool SetTimeScale(float value)
        {
            if (!BattleLoop.Running) return true;
            Boundaries.Hit("frame-clock");
            BattleClock.TimeScale = value;
            return false;
        }

        private static bool Answer(float value, ref float result)
        {
            if (!BattleLoop.Running) return false;
            Boundaries.Hit("frame-clock");
            result = value;
            return true;
        }
    }
}
