using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The view prefab the game would instantiate for a unit, read on demand from the game's own <c>.unit</c> bundle: the
    /// serialized fields of its UnitEntityView (corpulence, rotation lock, ...), UnitMovementAgent (acceleration, speeds,
    /// avoidance) and OverrideSizeScale. The prefab is chosen as <c>UnitEntityData.CreateView</c> chooses it. Only the
    /// type trees are kept; models, animators and colliders are not loaded. A scenario can replace the corpulence by size
    /// (<see cref="BattleScenario.CorpulenceOverrideBySize"/>) and give one for bundles the inputs lack
    /// (<see cref="BattleScenario.CorpulenceFallbackBySize"/>).
    /// </summary>
    internal sealed class UnitViewPrefab
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, UnitViewPrefab> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Type> ViewTypes = new(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<Size, float> None = new Dictionary<Size, float>();

        private readonly List<(string ClassName, AssetTypeValueField Field)> components;
        private readonly float? corpulence;

        private UnitViewPrefab(string assetId, string bundle, string name, List<(string, AssetTypeValueField)> components)
        {
            AssetId = assetId;
            Bundle = bundle;
            Name = name;
            this.components = components;
            Source = "prefab";
        }

        private UnitViewPrefab(UnitViewPrefab prefab, Size size, float corpulence, string source)
        {
            AssetId = prefab?.AssetId;
            Bundle = prefab?.Bundle;
            Name = prefab?.Name;
            components = prefab?.components ?? new List<(string, AssetTypeValueField)>();
            Size = size;
            this.corpulence = corpulence;
            Source = source;
        }

        /// <summary>The corpulence overrides of the battle being built or run.</summary>
        internal static IReadOnlyDictionary<Size, float> Overrides { get; private set; } = None;

        /// <summary>The corpulence fallbacks of the battle being built or run.</summary>
        internal static IReadOnlyDictionary<Size, float> Fallbacks { get; private set; } = None;

        internal static void Configure(IDictionary<Size, float> overrides, IDictionary<Size, float> fallbacks)
        {
            Overrides = new Dictionary<Size, float>(overrides);
            Fallbacks = new Dictionary<Size, float>(fallbacks);
        }

        internal static void ClearConfigured()
        {
            Overrides = None;
            Fallbacks = None;
        }

        /// <summary>The prefab's asset id; null when no bundle was read.</summary>
        public string AssetId { get; }
        public string Bundle { get; }
        public string Name { get; }

        /// <summary>
        /// Where the view comes from: <c>prefab</c>; <c>prefab, corpulence override</c>; <c>corpulence override</c> or
        /// <c>corpulence fallback</c> when the bundle is not in the inputs.
        /// </summary>
        public string Source { get; }

        /// <summary>The size whose configured corpulence is used; null when the prefab's own is.</summary>
        public Size? Size { get; }

        /// <summary>True when the corpulence is configured instead of the prefab's own.</summary>
        public bool ReplacesCorpulence => corpulence.HasValue;

        /// <summary>The class of the prefab's UnitEntityView component, which may be a subclass.</summary>
        public string ViewClass => components.FirstOrDefault(c => ViewType(c.ClassName) != null).ClassName ?? nameof(UnitEntityView);

        /// <summary>The UnitEntityView type tree; null when no bundle was read.</summary>
        public AssetTypeValueField View => Component(components.FirstOrDefault(c => ViewType(c.ClassName) != null).ClassName);
        public AssetTypeValueField MovementAgent => Component(nameof(UnitMovementAgent));
        public AssetTypeValueField SizeOverride => Component(nameof(OverrideSizeScale));

        /// <summary>The corpulence the view gets as its serialized value: the configured one, else the prefab's.</summary>
        public float SerializedCorpulence => corpulence
            ?? (View?["m_Corpulence"] is { IsDummy: false } serialized ? serialized.AsFloat : 0.5f);

        private AssetTypeValueField Component(string className)
            => className == null ? null : components.FirstOrDefault(c => c.ClassName == className).Field;

        /// <summary>The managed UnitEntityView type for a script class, or null when the class is no unit view.</summary>
        internal static Type ViewType(string className)
        {
            if (className == null) return null;
            lock (ViewTypes)
            {
                if (!ViewTypes.TryGetValue(className, out var type))
                    ViewTypes[className] = type = typeof(UnitEntityView).Assembly.GetTypes()
                        .FirstOrDefault(t => t.Name == className && typeof(UnitEntityView).IsAssignableFrom(t));
                return type;
            }
        }

        /// <summary>
        /// The prefab UnitEntityData.CreateView instantiates for <paramref name="unit"/> without an active polymorph (whose
        /// view <see cref="ForPolymorph"/> gives): the custom prefab or the blueprint's prefab (the optimization dummy for
        /// units with a dummy view), then the legacy-unit override when no bundle holds it. Configured corpulence is looked
        /// up by the unit's original size. Units with doll data (characters made in character creation) need it: the game
        /// makes their view from the doll room character, which battles do not read.
        /// </summary>
        internal static UnitViewPrefab For(UnitEntityData unit)
        {
            var size = unit.Descriptor.OriginalSize;
            if (unit.Get<UnitPartDollData>()?.Default?.RacePreset != null)
                return WithoutBundle(unit, size, "doll data from character creation")
                    ?? throw new NotSupportedException($"{unit} has doll data from character creation, whose view battles do not read. Set BattleScenario.CorpulenceOverrideBySize or CorpulenceFallbackBySize for Size.{size}.");
            var assetId = string.IsNullOrEmpty(unit.Descriptor.CustomPrefabGuid) ? unit.Blueprint.Prefab?.AssetId : unit.Descriptor.CustomPrefabGuid;
            if (unit.HasDummyView) assetId = BlueprintRoot.Instance.OptimizationDummyUnit?.AssetId;
            if (Resolve(assetId, unit, size) is { } prefab) return prefab;
            if (OverrideLegacyUnits.Instance is { } overrides && overrides.TryGetOverrideGuid(assetId, out var replacement) && Resolve(replacement, unit, size) is { } overridden)
                return overridden;
            throw new InvalidOperationException(
                $"The game finds no unit view prefab for {unit} (asset '{assetId}'): it logs \"Cannot find prefab\" and the unit gets no view, so it cannot act in a battle.");
        }

        /// <summary>
        /// Polymorph.CreatePolymorphView: the polymorph's prefab for the unit's gender, or null when it has none or no bundle
        /// lists it, as the game then keeps the unit's view. Configured corpulence is looked up by the unit's size at that
        /// moment; polymorphs into a doll need it.
        /// </summary>
        internal static UnitViewPrefab ForPolymorph(UnitEntityData unit, Kingmaker.UnitLogic.Buffs.Polymorph polymorph)
        {
            var size = unit.State.Size;
            if (polymorph.GetDollData(unit) != null)
                return WithoutBundle(unit, size, "polymorph into a doll")
                    ?? throw new NotSupportedException($"{unit} polymorphs into a doll ({polymorph.OwnerBlueprint?.name}), whose view battles do not read. Set BattleScenario.CorpulenceOverrideBySize or CorpulenceFallbackBySize for Size.{size}.");
            return Resolve(polymorph.GetPrefab(unit)?.AssetId, unit, size);
        }

        /// <summary>
        /// The prefab under <paramref name="assetId"/> with the override for <paramref name="size"/> applied; when its bundle
        /// is not in the inputs, the override or else the fallback without a prefab, else environment-missing. Null when no
        /// bundle lists the asset or it has no UnitEntityView, as the game then finds no view.
        /// </summary>
        private static UnitViewPrefab Resolve(string assetId, UnitEntityData unit, Size size)
        {
            if (string.IsNullOrEmpty(assetId) || !GameBundles.TryLocate(assetId, out var bundle)) return null;
            var path = Path.Combine(HarnessState.Inputs.Bundles, bundle);
            if (!File.Exists(path))
                return WithoutBundle(unit, size, "bundle not in the inputs")
                    ?? throw new OfflineEnvironmentMissingException(
                        $"Bundles/{bundle}, the view prefab of {unit.Blueprint?.name} ({size}), is not in the inputs ({path}). Run against the game installation, or set BattleScenario.CorpulenceFallbackBySize[Size.{size}] (used only when a bundle is missing) or CorpulenceOverrideBySize[Size.{size}].");
            var prefab = Read(assetId, bundle, path);
            if (prefab == null) return null;
            return Overrides.TryGetValue(size, out var corpulence) ? new UnitViewPrefab(prefab, size, corpulence, "prefab, corpulence override") : prefab;
        }

        /// <summary>A view without a prefab: the override for the size, else its fallback; null when neither is set.</summary>
        private static UnitViewPrefab WithoutBundle(UnitEntityData unit, Size size, string reason)
        {
            if (Overrides.TryGetValue(size, out var corpulence)) return new UnitViewPrefab(null, size, corpulence, "corpulence override (" + reason + ")");
            if (Fallbacks.TryGetValue(size, out corpulence)) return new UnitViewPrefab(null, size, corpulence, "corpulence fallback (" + reason + ")");
            return null;
        }

        private static UnitViewPrefab Read(string assetId, string bundle, string path)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(assetId, out var cached)) return cached;
                var read = UnityAssetMapper.ReadPrefabComponents(path, assetId);
                var prefab = read is not { } found || !found.Components.Any(c => ViewType(c.ClassName) != null)
                    ? null
                    : new UnitViewPrefab(assetId, bundle, found.Name, found.Components);
                Cache[assetId] = prefab;
                return prefab;
            }
        }
    }
}
