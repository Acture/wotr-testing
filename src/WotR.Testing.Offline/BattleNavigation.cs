using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HarmonyLib;
using Kingmaker.Blueprints.Area;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The navigation graph of a battle's area, loaded as the game's NavMeshManager does: the area part's GraphCache
    /// names a TextAsset, the game's Bundles/locationlist.json names the .nav bundle that holds it, and the game's A*
    /// code deserializes it (<c>AstarPath.active.data.DeserializeGraphs</c>). AstarPath.active is a component without a
    /// GameObject with the serialized settings of the AstarPath in the area part's mechanics scene, which is the active
    /// one once the game has loaded the area; path requests run on the game's own non-threaded path processor. No scene
    /// geometry is loaded.
    /// </summary>
    /// <remarks>
    /// A* limits its work per frame by the computer's clock: <c>maxFrameTime</c> milliseconds of searching, after which a
    /// search or the rest of the queue waits for the next frame, and 0.5 ms of handing finished paths back. In a battle a
    /// frame does all of it, as a machine fast enough for both limits does, so the frame a path finishes in follows the
    /// game's own order and not the machine's speed or load (boundary pathfinding-clock).
    /// </remarks>
    internal sealed class BattleNavigation
    {
        /// <summary>
        /// A search budget no frame reaches (10^12 ms, about 31 years). A* converts it to clock ticks and adds the current
        /// time, which stays well within a long; an infinite budget would not.
        /// </summary>
        internal const float UnlimitedFrameTime = 1e12f;

        private static readonly MethodInfo ClearGraphs = AccessTools.Method(typeof(Pathfinding.AstarData), "ClearGraphs");
        private static readonly MethodInfo AstarUpdate = AccessTools.Method(typeof(AstarPath), "Update");
        private static readonly FieldInfo ReturnQueue = AccessTools.Field(typeof(AstarPath), "pathReturnQueue");
        private static readonly MethodInfo ReturnPaths = AccessTools.Method(AccessTools.TypeByName("Pathfinding.PathReturnQueue"), "ReturnPaths");
        private static readonly object Gate = new();
        private static readonly Dictionary<string, byte[]> Graphs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (string Source, AssetTypeValueField Settings)> SceneSettings = new(StringComparer.OrdinalIgnoreCase);
        private static SceneNavigationSettings.Snapshot snapshotSettings;

        private readonly AstarPath astar;
        private readonly AstarPath previous;

        private BattleNavigation(string part, string assetId, string bundle, string bundleSha256, AstarPath astar, AstarPath previous,
            string settingsSource, IReadOnlyList<string> settingsGaps, float sceneFrameTime)
        {
            SceneFrameTime = sceneFrameTime;
            SettingsSource = settingsSource;
            SettingsGaps = settingsGaps;
            Part = part;
            AssetId = assetId;
            Bundle = bundle;
            BundleSha256 = bundleSha256;
            this.astar = astar;
            this.previous = previous;
            GraphTypes = astar.data.graphs.Where(g => g != null).Select(g => g.GetType().Name).ToList();
            var nodes = 0;
            foreach (var graph in astar.data.graphs) graph?.GetNodes(_ => nodes++);
            Nodes = nodes;
        }

        public string Part { get; }
        public string AssetId { get; }
        public string Bundle { get; }
        public string BundleSha256 { get; }
        public IReadOnlyList<string> GraphTypes { get; }
        public int Nodes { get; }

        /// <summary>Where the A* settings came from: the mechanics scene's bundle, or why the class defaults apply.</summary>
        public string SettingsSource { get; }

        /// <summary>Serialized references in the scene's AstarPath that stay unset.</summary>
        public IReadOnlyList<string> SettingsGaps { get; }

        /// <summary>The scene's <c>maxFrameTime</c>, in milliseconds of the computer's clock, which a battle does not apply.</summary>
        public float SceneFrameTime { get; }

        internal AstarPath Astar => astar;

        internal static BattleNavigation Load(BlueprintAreaPart part)
        {
            var assetId = part.GraphCache?.AssetId;
            if (string.IsNullOrEmpty(assetId))
                throw new InvalidOperationException($"Area {part.name} has no navigation graph (BlueprintAreaPart.GraphCache is empty); choose an area the game navigates.");
            if (!GameBundles.TryLocate(assetId, out var bundle))
                throw new InvalidOperationException($"Bundles/locationlist.json does not list the navigation graph {assetId} of {part.name}.");
            var bundlePath = GameBundles.PathOf(bundle, "navigation graph of " + part.name);
            var bytes = ReadGraph(bundlePath, assetId);

            var astar = (AstarPath)Activator.CreateInstance(typeof(AstarPath), nonPublic: true);
            var (settingsSource, settings) = ReadSceneSettings(part);
            var settingsGaps = new List<string>();
            if (settings != null)
            {
                // As Unity loads the scene's component: its serialized fields; the graphs come from the GraphCache below.
                var mapper = UnityAssetMapper.Detached();
                mapper.MapInto(settings, astar);
                settingsGaps.AddRange(mapper.Gaps.Keys);
            }
            else
            {
                OfflineRuntime.Warn($"Battle area {part.name}: A* uses its class defaults ({settingsSource}) instead of the settings of the area's AstarPath, "
                    + "for example its nearest-node search; paths can differ from the game's.");
            }
            var sceneFrameTime = astar.maxFrameTime;
            astar.maxFrameTime = UnlimitedFrameTime;
            Boundaries.Hit("pathfinding-clock");
            OfflineUnitView.MarkLive(astar);
            var previous = AstarPath.active;
            AstarPath.active = astar;
            try
            {
                astar.data = new Pathfinding.AstarData();
                astar.data.DeserializeGraphs(bytes);
                Boundaries.Hit("navigation-graph");
                if (astar.data.graphs == null || astar.data.graphs.All(g => g == null))
                    throw new InvalidOperationException($"The game's A* code read no graph from {bundle} ({assetId}); see the game log in the report.");
                return new BattleNavigation(part.name, assetId, bundle, GameBundles.Sha256(bundle), astar, previous, settingsSource, settingsGaps, sceneFrameTime);
            }
            catch
            {
                // A failed load leaves A* as it was: the previous object active, this one dead.
                if (ReferenceEquals(AstarPath.active, astar)) AstarPath.active = previous;
                OfflineUnitView.MarkDead(astar);
                throw;
            }
        }

        /// <summary>
        /// One Unity Update of the AstarPath component: graph updates, queued path requests and their callbacks. The paths
        /// its 0.5 ms time slice leaves in the return queue go back right after it, in the same frame and order, as with no
        /// time slice. Paths their callbacks request wait for the next frame's search, as in the game.
        /// </summary>
        internal void Update()
        {
            AstarUpdate.Invoke(astar, null);
            ReturnPaths.Invoke(ReturnQueue.GetValue(astar), new object[] { false });
            Boundaries.Hit("pathfinding-clock");
        }

        internal void Unload()
        {
            try { ClearGraphs.Invoke(astar.data, null); }
            finally
            {
                if (ReferenceEquals(AstarPath.active, astar)) AstarPath.active = previous;
                OfflineUnitView.MarkDead(astar);
            }
        }

        /// <summary>
        /// The serialized fields, except the graph data, of the AstarPath component in the area part's mechanics scene
        /// (<c>BlueprintAreaPart.DynamicScene</c>, bundled as the game's BundledSceneLoader names it), read on demand from
        /// the scene's bundle. A snapshot holds no scene bundles; New-WotrSnapshot.ps1 keeps these fields of every mechanics
        /// scene in its navigation settings file instead. Null with the reason when the inputs have neither.
        /// </summary>
        private static (string Source, AssetTypeValueField Settings) ReadSceneSettings(BlueprintAreaPart part)
        {
            var scene = part.DynamicScene?.SceneName;
            if (string.IsNullOrEmpty(scene)) return ("the area part names no mechanics scene", null);
            var bundle = scene.ToLowerInvariant() + ".scenes";
            lock (Gate)
            {
                if (!SceneSettings.TryGetValue(bundle, out var settings))
                {
                    var path = Path.Combine(HarnessState.Inputs.Bundles, bundle);
                    settings = File.Exists(path) ? FromBundle(part, bundle, path) : FromSnapshot(part, bundle);
                    SceneSettings[bundle] = settings;
                }
                return settings;
            }
        }

        private static (string, AssetTypeValueField) FromBundle(BlueprintAreaPart part, string bundle, string path)
            => ("Bundles/" + bundle + " (sha256 " + GameBundles.Sha256(bundle) + ")",
                Single(SceneNavigationSettings.Read(path), $"Bundles/{bundle}, the mechanics scene of {part.name},"));

        private static (string, AssetTypeValueField) FromSnapshot(BlueprintAreaPart part, string bundle)
        {
            var file = Path.Combine(HarnessState.Inputs.InputRoot, SceneNavigationSettings.FileName);
            if (!File.Exists(file)) return ($"the inputs lack Bundles/{bundle}", null);
            if (snapshotSettings == null)
            {
                var loaded = SceneNavigationSettings.Load(file);
                // As with the snapshot's other files, never mix game versions.
                if (loaded.GameVersion != HarnessState.Inputs.Identity.GameVersion)
                    throw new OfflineEnvironmentMissingException($"{SceneNavigationSettings.FileName} was read from game version '{loaded.GameVersion}', the inputs are '{HarnessState.Inputs.Identity.GameVersion}'. Make the snapshot again.");
                snapshotSettings = loaded;
            }
            if (!snapshotSettings.Scenes.TryGetValue(bundle, out var entry))
                return ($"the inputs lack Bundles/{bundle}, and {SceneNavigationSettings.FileName}, which holds the scenes {SceneNavigationSettings.ScenePattern}, does not list it", null);
            return ($"{SceneNavigationSettings.FileName}, read from Bundles/{bundle} (sha256 {entry.Sha256})",
                Single(entry.AstarPaths, $"Bundles/{bundle} (as {SceneNavigationSettings.FileName} keeps it), the mechanics scene of {part.name},"));
        }

        private static AssetTypeValueField Single(IReadOnlyList<AssetTypeValueField> found, string scene)
            => found.Count == 1 ? found[0]
                : throw new InvalidOperationException($"{scene} has {found.Count} AstarPath components; the game uses the one its scene holds.");

        /// <summary>The TextAsset the bundle's container lists under the asset id, as the game's bundle loader finds it.</summary>
        private static byte[] ReadGraph(string bundlePath, string assetId)
        {
            lock (Gate)
            {
                if (Graphs.TryGetValue(assetId, out var cached)) return cached;
                var manager = new AssetsManager();
                try
                {
                    var bundle = manager.LoadBundleFile(bundlePath, true);
                    var file = manager.LoadAssetsFileFromBundle(bundle, 0, false);
                    foreach (var info in file.file.GetAssetsOfType(AssetClassID.AssetBundle))
                    {
                        foreach (var entry in manager.GetBaseField(file, info)["m_Container"]["Array"].Children)
                        {
                            if (!string.Equals(entry["first"].AsString, assetId, StringComparison.OrdinalIgnoreCase)) continue;
                            var asset = file.file.GetAssetInfo(entry["second"]["asset"]["m_PathID"].AsLong);
                            var bytes = manager.GetBaseField(file, asset)["m_Script"].AsByteArray;
                            Graphs[assetId] = bytes;
                            return bytes;
                        }
                    }
                }
                finally
                {
                    manager.UnloadAll(true);
                }
                throw new InvalidOperationException($"{Path.GetFileName(bundlePath)} has no asset {assetId}.");
            }
        }
    }
}
