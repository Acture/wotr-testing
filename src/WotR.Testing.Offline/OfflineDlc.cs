using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.DLC;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Which DLCs count as available offline. The game asks the store (Steam, GOG, ...), which needs the Unity player;
    /// instead the test project chooses with WotrDlc / WOTR_DLC: "all" (default), "none", "local" (DLCs installed by the
    /// Steam library that holds the inputs) or a comma-separated list of DLC blueprint names such as "Dlc4,Dlc6".
    /// </summary>
    public static class OfflineDlc
    {
        public const string Variable = "WOTR_DLC";

        private static readonly ConcurrentDictionary<string, DlcQuery> Queries = new();
        private static readonly ConcurrentQueue<ModBlueprint> ModBlueprints = new();
        private static HashSet<string> names;
        private static HashSet<long> steamIds;

        /// <summary>The configured policy: all, none, local or list.</summary>
        public static string Policy { get; private set; } = "all";

        /// <summary>Steam DLC app IDs installed in the local Steam library (policy local only).</summary>
        public static IReadOnlyCollection<long> LocalSteamDlcIds => steamIds ?? (IReadOnlyCollection<long>)Array.Empty<long>();

        /// <summary>The DLC blueprint names listed by the test project (policy list only).</summary>
        public static IReadOnlyCollection<string> ListedDlcs => names ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        internal static void Configure(OfflineInputs inputs)
        {
            var value = (inputs.Dlc ?? "").Trim();
            Boundaries.Declare("dlc-availability", "store", "BlueprintDlc.IsAvailable and StoreManager DLC refreshes (store ownership checks)",
                "Answered from WotrDlc instead of Steam/GOG/Epic: all, none, local (installed in the Steam library) or a list of DLC names.");
            switch (value.ToLowerInvariant())
            {
                case "":
                case "all":
                    Policy = "all";
                    break;
                case "none":
                    Policy = "none";
                    break;
                case "local":
                    Policy = "local";
                    steamIds = ReadSteamDlcIds(inputs.InputRoot);
                    break;
                default:
                    Policy = "list";
                    names = new HashSet<string>(value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                    break;
            }
        }

        internal static bool IsAvailable(BlueprintDlc dlc)
        {
            var available = Policy switch
            {
                "all" => true,
                "none" => false,
                "local" => dlc.ComponentsArray?.OfType<DlcStoreSteam>().Any(s => steamIds.Contains(Convert.ToInt64(s.SteamId))) == true,
                _ => names.Contains(dlc.name),
            };
            var query = Queries.GetOrAdd(dlc.name, n => new DlcQuery { Dlc = n, Available = available });
            System.Threading.Interlocked.Increment(ref query.CountField);
            return available;
        }

        internal static void RecordModBlueprint(BlueprintGuid guid, SimpleBlueprint blueprint, bool replacesExisting)
        {
            var rewards = (blueprint as BlueprintScriptableObject)?.ComponentsArray?.OfType<DlcCondition>()
                .Select(c => c.DlcReward?.name ?? "unknown").Distinct().ToArray() ?? Array.Empty<string>();
            ModBlueprints.Enqueue(new ModBlueprint { Guid = guid.ToString(), Name = blueprint?.name, Replaces = replacesExisting, DlcRewards = rewards });
        }

        /// <summary>For environment.json: the policy, every DLC the game or mods asked about, and DLC-gated mod blueprints.</summary>
        public static object Report()
        {
            var blueprints = ModBlueprints.ToArray();
            return new
            {
                policy = Policy,
                listed = ListedDlcs.OrderBy(n => n).ToArray(),
                listedButNeverQueried = ListedDlcs.Where(n => !Queries.ContainsKey(n)).OrderBy(n => n).ToArray(),
                localSteamDlcIds = LocalSteamDlcIds.OrderBy(i => i).ToArray(),
                queried = Queries.Values.OrderBy(q => q.Dlc).Select(q => new { dlc = q.Dlc, available = q.Available, count = q.CountField }).ToArray(),
                modBlueprints = new
                {
                    added = blueprints.Count(b => !b.Replaces),
                    replacedVanilla = blueprints.Count(b => b.Replaces),
                    dlcGated = blueprints.Where(b => b.DlcRewards.Length > 0)
                        .Select(b => new { guid = b.Guid, name = b.Name, replacesVanilla = b.Replaces, dlcRewards = b.DlcRewards }).ToArray(),
                },
            };
        }

        /// <summary>Installed DLC depots from the Steam app manifest of the library that contains the game root.</summary>
        private static HashSet<long> ReadSteamDlcIds(string inputRoot)
        {
            var game = new DirectoryInfo(inputRoot);
            var steamapps = game.Parent?.Parent;
            if (game.Parent?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true || steamapps == null)
                throw new OfflineEnvironmentMissingException($"WotrDlc=local needs a Steam installation (steamapps/common/<game>); {inputRoot} is not one.");
            foreach (var manifest in steamapps.GetFiles("appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest.FullName);
                if (!Regex.IsMatch(text, $"\"installdir\"\\s+\"{Regex.Escape(game.Name)}\"", RegexOptions.IgnoreCase)) continue;
                return new HashSet<long>(Regex.Matches(text, "\"dlcappid\"\\s+\"(\\d+)\"").Cast<Match>().Select(m => long.Parse(m.Groups[1].Value)));
            }
            throw new OfflineEnvironmentMissingException($"WotrDlc=local: no Steam app manifest in {steamapps.FullName} installs {game.Name}.");
        }

        private sealed class DlcQuery
        {
            public string Dlc;
            public bool Available;
            public long CountField;
        }

        private sealed class ModBlueprint
        {
            public string Guid;
            public string Name;
            public bool Replaces;
            public string[] DlcRewards;
        }
    }

    [HarmonyPatch(typeof(BlueprintDlc), nameof(BlueprintDlc.IsAvailable), MethodType.Getter)]
    internal static class DlcAvailabilityPatch
    {
        private static bool Prefix(BlueprintDlc __instance, ref bool __result)
        {
            Boundaries.Hit("dlc-availability");
            __result = OfflineDlc.IsAvailable(__instance);
            return false;
        }
    }

    /// <summary>
    /// Store refreshes would ask Steam/GOG/Epic through the player; there is no store offline, so they do nothing and
    /// availability comes from <see cref="DlcAvailabilityPatch"/>. Mods such as TabletopTweaks refresh before reading it.
    /// </summary>
    [HarmonyPatch]
    internal static class DlcStoreRefreshPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Kingmaker.Stores.StoreManager), "RefreshDLCs");
            yield return AccessTools.Method(typeof(Kingmaker.Stores.StoreManager), "RefreshAllDLCStatuses");
        }

        private static bool Prefix()
        {
            Boundaries.Hit("dlc-availability");
            return false;
        }
    }

    /// <summary>Observes blueprints added to the cache after the vanilla pack (by mods); does not change them.</summary>
    [HarmonyPatch(typeof(Kingmaker.Blueprints.JsonSystem.BlueprintsCache), nameof(Kingmaker.Blueprints.JsonSystem.BlueprintsCache.AddCachedBlueprint))]
    internal static class ModBlueprintObserver
    {
        internal static bool Active;

        private static void Prefix(Kingmaker.Blueprints.JsonSystem.BlueprintsCache __instance, BlueprintGuid __0, SimpleBlueprint __1)
        {
            if (!Active) return;
            var loaded = Traverse.Create(__instance).Field("m_LoadedBlueprints").GetValue() as System.Collections.IDictionary;
            OfflineDlc.RecordModBlueprint(__0, __1, loaded?.Contains(__0) == true);
        }
    }
}
