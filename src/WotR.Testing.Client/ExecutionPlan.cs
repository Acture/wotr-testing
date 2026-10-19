using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>One environment and workload of a run: offgame or ingame, ordinary tests or fuzz targets.</summary>
    public sealed class Partition
    {
        public Partition(string mode, string kind, int declared)
        {
            Mode = mode;
            Kind = kind;
            Declared = declared;
        }

        public string Mode { get; }
        public string Kind { get; }
        /// <summary>Cases or targets the assembly declares for this partition, from metadata.</summary>
        public int Declared { get; }
        public string Name => Mode + "/" + Kind;
        public override string ToString() => Name;
    }

    /// <summary>
    /// The immutable scope of one run, resolved before any worker or game starts: explicit option, then project property,
    /// then the defaults auto/all, which run every partition the assembly declares.
    /// </summary>
    public sealed class ExecutionPlan
    {
        public const string Auto = "auto";
        public const string All = "all";

        private ExecutionPlan() { }

        public string RequestedMode { get; private set; }
        public string RequestedKind { get; private set; }
        public IReadOnlyList<Partition> Partitions { get; private set; }
        /// <summary>Declared partitions the requested scope leaves out.</summary>
        public IReadOnlyList<Partition> Excluded { get; private set; }
        public int? FuzzSeed { get; private set; }
        public ReplaySelection Replay { get; private set; }

        /// <summary>A plan resolved earlier, by the build task for the VSTest adapter.</summary>
        public static ExecutionPlan FromPartitions(string mode, string kind, IReadOnlyList<Partition> partitions, int? fuzzSeed, ReplaySelection replay)
            => new ExecutionPlan {
                RequestedMode = mode, RequestedKind = kind, Partitions = partitions, Excluded = new List<Partition>(), FuzzSeed = fuzzSeed, Replay = replay
            };

        public static ExecutionPlan Resolve(TestScope scope, string mode, string kind, int? fuzzSeed, string replayFile, string offgameRuntime, bool inGameOptionsGiven)
        {
            mode = string.IsNullOrEmpty(mode) ? Auto : mode;
            kind = string.IsNullOrEmpty(kind) ? All : kind;
            if (mode != Auto && mode != Welcome.OffGame && mode != Welcome.InGame) throw new PlanException("WotrTestMode must be auto, offgame or ingame, not " + mode + ".");
            if (kind != All && kind != TestDescriptor.TestKind && kind != TestDescriptor.FuzzKind) throw new PlanException("WotrTestKind must be all, tests or fuzz, not " + kind + ".");
            if (scope.Errors.Count > 0) throw new PlanException(string.Join("\n", scope.Errors));
            ReplaySelection replay = null;
            if (!string.IsNullOrEmpty(replayFile)) {
                if (kind == TestDescriptor.TestKind) throw new PlanException("A fuzz replay cannot be combined with the tests-only selection.");
                replay = ReplaySelection.Read(replayFile);
                if (mode != Auto && mode != replay.Mode) throw new PlanException("The replay case was recorded " + replay.Mode + "; it cannot run " + mode + ".");
                // A replay selects exactly the recorded target in the recorded environment.
                mode = replay.Mode;
                kind = TestDescriptor.FuzzKind;
            }
            if (fuzzSeed.HasValue && kind == TestDescriptor.TestKind) throw new PlanException("A fuzz seed cannot be combined with the tests-only selection.");
            if (mode == Welcome.InGame && offgameRuntime == "netfx") throw new PlanException("WotrRuntime=netfx selects an offgame runtime; it cannot apply to an ingame-only run.");
            if (mode == Welcome.OffGame && inGameOptionsGiven) throw new PlanException("In-game connection options cannot apply to an offgame-only run.");
            var declared = new List<Partition>();
            foreach (var environment in new[] { Welcome.OffGame, Welcome.InGame })
                foreach (var workload in new[] { TestDescriptor.TestKind, TestDescriptor.FuzzKind })
                    if (scope.Count(environment, workload) > 0) declared.Add(new Partition(environment, workload, scope.Count(environment, workload)));
            bool Requested(Partition partition) => (mode == Auto || mode == partition.Mode) && (kind == All || kind == partition.Kind);
            var plan = new ExecutionPlan {
                RequestedMode = mode, RequestedKind = kind, FuzzSeed = fuzzSeed, Replay = replay,
                Partitions = declared.Where(Requested).ToList(), Excluded = declared.Where(partition => !Requested(partition)).ToList()
            };
            if (plan.Partitions.Count == 0)
                throw new PlanException("Nothing to run: the assembly declares " + (declared.Count == 0 ? "no tests or fuzz targets" : string.Join(", ", declared)) +
                    ", and the requested scope is mode " + mode + ", kind " + kind + ".");
            return plan;
        }
    }

    public sealed class PlanException : Exception
    {
        public PlanException(string message) : base("[PLAN] " + message) { }
    }

    /// <summary>The selection part of a saved fuzz replay file; the case itself is passed to the target unchanged.</summary>
    [DataContract]
    public sealed class ReplaySelection
    {
        [DataMember(Name = "FormatVersion")] public int FormatVersion { get; set; }
        [DataMember(Name = "Target")] public string Target { get; set; }
        [DataMember(Name = "Mode")] public string Mode { get; set; }
        [DataMember(Name = "Case")] public string Case { get; set; }

        public static ReplaySelection Read(string path)
        {
            ReplaySelection selection;
            try { selection = ProtocolJson.Deserialize<ReplaySelection>(File.ReadAllText(path)); }
            catch (Exception error) when (error is IOException || error is ProtocolException) { throw new PlanException("The fuzz replay file cannot be read: " + error.Message); }
            if (selection.FormatVersion != 1 || string.IsNullOrEmpty(selection.Target) || string.IsNullOrEmpty(selection.Case) ||
                (selection.Mode != Welcome.OffGame && selection.Mode != Welcome.InGame))
                throw new PlanException("The fuzz replay file " + path + " is not a format 1 replay with a target, a mode and a case.");
            return selection;
        }
    }
}
