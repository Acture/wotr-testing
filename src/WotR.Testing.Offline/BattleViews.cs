using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<object, UnitEntityData> BoundsColliders = new(ReferenceComparer.Instance);
        private static readonly Dictionary<object, float> Radii = new(ReferenceComparer.Instance);

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
            if (!Enabled.TryGetValue(agent, out var enabled) || enabled) AllAgents.Add(agent);
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
            return gameObject;
        }

        private static UnityEngine.Object Create(Type type, StandIn standIn, GameObject gameObject, Transform transform)
        {
            var value = (UnityEngine.Object)Activator.CreateInstance(type, nonPublic: true);
            MarkLive(value);
            standIn.Parts.Add(value);
            GameObjects[value] = gameObject;
            if (transform != null) ComponentTransforms[value] = transform;
            if (value is Behaviour) Enabled[value] = true;
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
            foreach (var part in standIn.Parts)
            {
                BoundsColliders.Remove(part);
                Radii.Remove(part);
                Transforms.Remove(part);
                StoredPositions.Remove(part);
                ComponentTransforms.Remove(part);
                GameObjects.Remove(part);
                Enabled.Remove(part);
                MarkDead(part);
            }
            StandIns.Remove(view);
        }

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

        internal static bool TryGetEnabled(Behaviour behaviour, out bool enabled) => Enabled.TryGetValue(behaviour, out enabled);

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
                if ((center - closest).magnitude <= radius + Radii[pair.Key]) results[count++] = (Collider)pair.Key;
            }
            return count;
        }

        /// <summary>
        /// A stand-in component's enabled flag. Unity runs OnEnable or OnDisable when the flag of an active component
        /// changes; for the movement agent, once it is set up, the game's own run, which add it to or remove it from the
        /// agents list and its obstacle group. The other stand-in components only hold the flag.
        /// </summary>
        internal static bool TrySetEnabled(Behaviour behaviour, bool enabled)
        {
            if (!Enabled.TryGetValue(behaviour, out var current)) return false;
            Enabled[behaviour] = enabled;
            if (current != enabled && behaviour is UnitMovementAgent { Unit: not null } agent)
                (enabled ? AgentOnEnable : AgentOnDisable).Invoke(agent, null);
            return true;
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
    /// Unity's frame clock and the part of it the game's <c>TimeController.Tick</c> advances in the default game mode.
    /// Offline no game mode runs and Unity has no frame clock, so a battle frame of 1/30 s first advances Unity's frame
    /// count and real time (<see cref="BeginFrame"/>), and then, in TimeController's place, sets the frame duration and
    /// advances game time as Tick does (<see cref="Tick"/>).
    /// </summary>
    internal static class BattleClock
    {
        internal const float FrameSeconds = 1f / 30f;

        private static int frameNumber;
        private static double realTime;
        private static bool justActivated;

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

        private static readonly MethodInfo SetDelta = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.TimeController), nameof(Kingmaker.Controllers.TimeController.DeltaTime));
        private static readonly MethodInfo SetGameDelta = AccessTools.PropertySetter(typeof(Kingmaker.Controllers.TimeController), nameof(Kingmaker.Controllers.TimeController.GameDeltaTime));

        internal static void Set(float delta, float gameDelta)
        {
            var time = Kingmaker.Game.Instance.TimeController;
            SetDelta.Invoke(time, new object[] { delta });
            SetGameDelta.Invoke(time, new object[] { gameDelta });
        }

        /// <summary>TimeController.Activate: the first tick afterwards takes Unity's unscaled frame time.</summary>
        internal static void Activate() => justActivated = true;

        /// <summary>The start of a Unity frame: the frame count and real time advance before any Update runs.</summary>
        internal static void BeginFrame()
        {
            frameNumber++;
            realTime += FrameSeconds;
            Boundaries.Hit("frame-clock");
        }

        /// <summary>
        /// TimeController.Tick outside turn-based combat: <c>DeltaTime = min(PlayerTimeScale * DebugTimeScale / 12,
        /// Time.deltaTime)</c>, and game time grows by it. Unity's <c>Time.deltaTime</c> is the frame time scaled by
        /// <c>Time.timeScale</c>, which Tick sets to <c>PlayerTimeScale * DebugTimeScale * UiTimeScale</c>; the first
        /// tick after activation takes the unscaled frame time.
        /// </summary>
        internal static float Tick()
        {
            var time = Kingmaker.Game.Instance.TimeController;
            var frame = justActivated ? FrameSeconds : FrameSeconds * time.PlayerTimeScale * time.DebugTimeScale * time.UiTimeScale;
            justActivated = false;
            var delta = Math.Min(time.PlayerTimeScale * time.DebugTimeScale / 12f, frame);
            Set(delta, delta);
            Kingmaker.Game.Instance.Player.GameTime += TimeSpan.FromSeconds(delta);
            Boundaries.Hit("frame-clock");
            return delta;
        }
    }
}
