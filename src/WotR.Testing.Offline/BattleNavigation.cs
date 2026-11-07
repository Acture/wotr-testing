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
    /// GameObject; path requests run on the game's own non-threaded path processor. No scene geometry is loaded.
    /// </summary>
    internal sealed class BattleNavigation
    {
        private static readonly MethodInfo ClearGraphs = AccessTools.Method(typeof(Pathfinding.AstarData), "ClearGraphs");
        private static readonly MethodInfo AstarUpdate = AccessTools.Method(typeof(AstarPath), "Update");
        private static readonly object Gate = new();
        private static readonly Dictionary<string, byte[]> Graphs = new(StringComparer.OrdinalIgnoreCase);

        private readonly AstarPath astar;
        private readonly AstarPath previous;

        private BattleNavigation(string part, string assetId, string bundle, string bundleSha256, AstarPath astar, AstarPath previous)
        {
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
                return new BattleNavigation(part.name, assetId, bundle, GameBundles.Sha256(bundle), astar, previous);
            }
            catch
            {
                // A failed load leaves A* as it was: the previous object active, this one dead.
                if (ReferenceEquals(AstarPath.active, astar)) AstarPath.active = previous;
                OfflineUnitView.MarkDead(astar);
                throw;
            }
        }

        /// <summary>One Unity Update of the AstarPath component: graph updates, queued path requests and their callbacks.</summary>
        internal void Update() => AstarUpdate.Invoke(astar, null);

        internal void Unload()
        {
            try { ClearGraphs.Invoke(astar.data, null); }
            finally
            {
                if (ReferenceEquals(AstarPath.active, astar)) AstarPath.active = previous;
                OfflineUnitView.MarkDead(astar);
            }
        }

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
