using System;
using System.Collections.Generic;
using System.Linq;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// The test process as a battle worker. Battles reset inside the process; after every cleanup the game-wide state is
    /// compared with the state before the build. A cleanup that leaves anything marks the worker dirty: it refuses further
    /// battles and the run fails, because the process can no longer give a clean environment.
    /// </summary>
    /// <remarks>Holds only plain values, so the environment report can be written even when the game never started.</remarks>
    public static class BattleWorker
    {
        public const string DirtyMarker = "[BATTLE_WORKER_DIRTY]";

        /// <summary>How resets work in this version; recorded in environment.json.</summary>
        public const string ResetMethod = "in-process cleanup verified by a game-state residue check";

        private const int RecentCleanups = 50;
        private static readonly object Gate = new();
        private static readonly List<BattleCleanup> Cleanups = new();
        private static readonly List<KeyValuePair<string, string>> PlayerContext = new();
        private static string active;
        private static int started;
        private static int builds;
        private static int cleanups;
        private static int failedCleanups;
        private static int episodes;
        private static int episodeSteps;
        private static int endedEpisodes;
        private static int failedEpisodes;

        /// <summary>Why the worker is dirty, or null while it is clean.</summary>
        public static string DirtyReason { get; private set; }

        public static bool IsDirty => DirtyReason != null;

        internal static void Enter(string scenario)
        {
            lock (Gate)
            {
                if (DirtyReason != null)
                    throw new InvalidOperationException($"{DirtyMarker} This test process refuses new battles: an earlier cleanup was not clean ({DirtyReason}). Run the battle in a new process.");
                if (active != null)
                    throw new InvalidOperationException($"Battle {active} is still active; dispose it before starting {scenario}. Game state is process-wide, so only one battle runs at a time.");
                active = scenario;
                started++;
            }
        }

        internal static void Exit()
        {
            lock (Gate) active = null;
        }

        internal static void Record(BattleCleanup cleanup)
        {
            lock (Gate)
            {
                cleanups++;
                Cleanups.Add(cleanup);
                if (Cleanups.Count > RecentCleanups) Cleanups.RemoveAt(0);
                if (cleanup.Clean) return;
                failedCleanups++;
                DirtyReason ??= cleanup.ToString();
            }
        }

        internal static void NoteBuild()
        {
            lock (Gate) builds++;
        }

        /// <summary>A <see cref="BattleEnvironment"/> episode began.</summary>
        internal static void NoteEpisode()
        {
            lock (Gate) episodes++;
        }

        internal static void NoteEpisodeStep(bool ended)
        {
            lock (Gate)
            {
                episodeSteps++;
                if (ended) endedEpisodes++;
            }
        }

        internal static void NoteEpisodeFailure()
        {
            lock (Gate) failedEpisodes++;
        }

        internal static void NotePlayerContext(string name, string description)
        {
            lock (Gate) PlayerContext.Add(new KeyValuePair<string, string>(name, description));
        }

        private static readonly Dictionary<string, object> Navigation = new();

        /// <summary>Navigation graphs battles loaded: the game input each came from, with its hash.</summary>
        internal static void NoteNavigation(BattleNavigation navigation)
        {
            lock (Gate)
                Navigation[navigation.AssetId] = new
                {
                    part = navigation.Part,
                    assetId = navigation.AssetId,
                    bundle = "Bundles/" + navigation.Bundle,
                    sha256 = navigation.BundleSha256,
                    graphs = navigation.GraphTypes,
                    nodes = navigation.Nodes,
                    settings = navigation.SettingsSource,
                    settingsUnset = navigation.SettingsGaps,
                    nearestNodeSearch = new
                    {
                        full = navigation.Astar.fullGetNearestSearch,
                        prioritizeGraphs = navigation.Astar.prioritizeGraphs,
                        maxDistance = navigation.Astar.maxNearestNodeDistance,
                    },
                    heuristic = navigation.Astar.heuristic.ToString(),
                    heuristicScale = navigation.Astar.heuristicScale,
                    // Not applied: a battle frame does all of its path work (boundary pathfinding-clock).
                    sceneMaxFrameTime = navigation.SceneFrameTime,
                };
        }

        private static readonly Dictionary<string, object> UnitViews = new();

        /// <summary>
        /// Unit view prefabs battles read: the game input each came from, with its hash, the corpulence its view
        /// serializes, and the serialized fields that stay unset because they refer to models, colliders or other assets.
        /// Corpulence a scenario configured (override, or fallback for a bundle the inputs lack) is listed with its source.
        /// </summary>
        internal static void NoteUnitView(UnitViewPrefab prefab, IDictionary<string, int> gaps)
        {
            lock (Gate)
            {
                if (prefab.View == null)
                {
                    UnitViews[prefab.Source + ":" + prefab.Size + ":" + prefab.SerializedCorpulence] = new
                    {
                        source = prefab.Source,
                        size = prefab.Size?.ToString(),
                        serializedCorpulence = prefab.SerializedCorpulence,
                        movementAgent = "class defaults",
                    };
                    return;
                }
                UnitViews[prefab.Source + ":" + prefab.AssetId + ":" + prefab.SerializedCorpulence] = new
                {
                    source = prefab.Source,
                    size = prefab.Size?.ToString(),
                    assetId = prefab.AssetId,
                    name = prefab.Name,
                    viewClass = prefab.ViewClass,
                    bundle = "Bundles/" + prefab.Bundle,
                    sha256 = GameBundles.Sha256(prefab.Bundle),
                    serializedCorpulence = prefab.SerializedCorpulence,
                    movementAgent = prefab.MovementAgent != null,
                    sizeOverride = prefab.SizeOverride != null,
                    unmapped = gaps.Keys.ToArray(),
                };
            }
        }

        /// <summary>
        /// The worker's state for environment.json, written when the run ends. A battle still active then was never
        /// disposed: its units, controllers and area were never cleaned up, so the worker counts as dirty.
        /// </summary>
        internal static object Report()
        {
            lock (Gate)
            {
                if (started == 0) return null;
                var dirtyReason = DirtyReason
                    ?? (active == null ? null : $"battle {active} was still active when the run ended; it was never disposed, so its game state was never cleaned up");
                return new
                {
                    resetMethod = ResetMethod,
                    dirty = dirtyReason != null,
                    dirtyReason,
                    undisposedBattle = active,
                    battlesStarted = started,
                    builds,
                    cleanups,
                    failedCleanups,
                    environment = episodes == 0 ? null : new { episodes, steps = episodeSteps, ended = endedEpisodes, failed = failedEpisodes },
                    playerContext = PlayerContext.Select(pair => new { name = pair.Key, prepared = pair.Value }).ToArray(),
                    navigation = Navigation.Values.ToArray(),
                    unitViews = UnitViews.Values.ToArray(),
                    checkedState = new[]
                    {
                        "EventBus global and unit subscriptions", "Rulebook global, initiator and target subscriptions",
                        "EntityService entities", "game state and awake units", "Player.CrossSceneState entities", "player party inventory items, their counts and Player.Inventory identity", "game time",
                        "frame clock (DeltaTime, GameDeltaTime)", "unit view stand-ins",
                        "player combat state, party and main character", "game unit groups", "loaded area and the scene loader's current area", "entity-bounds scene",
                        "Game.Instance.LevelUpController", "turn-based combat controller current turn", "rulebook event stack",
                        "game RNG seed and call count", "temporary blueprints",
                    },
                    recentCleanups = Cleanups.Select(c => new
                    {
                        scenario = c.Scenario,
                        generation = c.Generation,
                        seed = c.Seed,
                        clean = c.Clean,
                        failures = c.Failures,
                        residue = c.Residue,
                    }).ToArray(),
                };
            }
        }
    }
}
