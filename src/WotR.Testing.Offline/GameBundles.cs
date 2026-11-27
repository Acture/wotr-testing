using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The game's asset bundles, located as its bundle loader does: <c>Bundles/locationlist.json</c> maps an asset id
    /// to the bundle file that holds it. Only bundle files a battle reads are opened, and each one's hash is recorded.
    /// </summary>
    internal static class GameBundles
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> locations;

        /// <summary>The bundle file that holds <paramref name="assetId"/>, or false when the game lists none.</summary>
        internal static bool TryLocate(string assetId, out string bundle)
        {
            bundle = null;
            return !string.IsNullOrEmpty(assetId) && Locations().TryGetValue(assetId, out bundle);
        }

        /// <summary>The full path of a bundle file; a missing file means the inputs lack it, for example an older snapshot.</summary>
        internal static string PathOf(string bundle, string purpose)
        {
            var path = Path.Combine(HarnessState.Inputs.Bundles, bundle);
            if (!File.Exists(path))
                throw new OfflineEnvironmentMissingException($"Bundle not found: {path} ({purpose}). Snapshots made before battles read it lack it; make a new snapshot. If the installation lacks it too, New-WotrSnapshot.ps1 lists it as missing.");
            return path;
        }

        internal static string Sha256(string bundle)
        {
            lock (Gate)
            {
                if (!Hashes.TryGetValue(bundle, out var hash))
                    Hashes[bundle] = hash = OfflineRuntime.FileSha256(Path.Combine(HarnessState.Inputs.Bundles, bundle));
                return hash;
            }
        }

        private static Dictionary<string, string> Locations()
        {
            lock (Gate)
            {
                if (locations != null) return locations;
                var path = Path.Combine(HarnessState.Inputs.Bundles, "locationlist.json");
                if (!File.Exists(path))
                    throw new OfflineEnvironmentMissingException($"Bundles/locationlist.json not found: {path}. Snapshots made before battles read bundles lack it; make a new snapshot.");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var guids = document.RootElement.GetProperty("m_Guids").EnumerateArray().Select(e => e.GetString()).ToList();
                var bundles = document.RootElement.GetProperty("m_Bundles").EnumerateArray().Select(e => e.GetString()).ToList();
                // The game's LocationList pairs them by position and fails when an asset id has no bundle.
                if (guids.Count > bundles.Count)
                    throw new InvalidOperationException($"{path} lists {guids.Count} asset ids but only {bundles.Count} bundles; the game's LocationList cannot read it. Copy the file from the installation again.");
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < guids.Count; i++) map[guids[i]] = bundles[i];
                return locations = map;
            }
        }
    }
}
