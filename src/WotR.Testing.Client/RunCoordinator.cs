using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    public sealed class RunOptions
    {
        public PartitionSettings Settings { get; set; }
        /// <summary>Fresh directory of this invocation; each partition reports in a subdirectory.</summary>
        public string ReportDirectory { get; set; }
        public string Connection { get; set; } = ConnectionModes.Auto;
        public int ProcessId { get; set; }
        public string InstallRoot { get; set; }
        public int MinimumPassed { get; set; } = 1;
        public bool LaunchInBackground { get; set; } = true;
    }

    public sealed class RunSummary
    {
        public List<PartitionResult> Partitions { get; } = new List<PartitionResult>();
        public List<string> Problems { get; } = new List<string>();
        /// <summary>How the run reached and left the game; `dotnet test` does not show these messages on success.</summary>
        public List<string> Log { get; } = new List<string>();
        public string ReportFile { get; set; }
        public bool Succeeded => Problems.Count == 0 && Partitions.All(partition => partition.Succeeded);
        public int Passed => Partitions.Sum(partition => partition.Passed);
        public int Failed => Partitions.Sum(partition => partition.Failed);
        public int Skipped => Partitions.Sum(partition => partition.Skipped);
    }

    /// <summary>
    /// Runs every partition of a plan in turn, each with fresh settings and its own report. A failed partition does not
    /// stop independent ones; cancellation does. The game is reached once for all in-game partitions, and a game this
    /// run started is closed at the end.
    /// </summary>
    public static class RunCoordinator
    {
        public static async Task<RunSummary> RunAsync(ExecutionPlan plan, RunOptions options, IPartitionObserver observer, CancellationToken cancellationToken)
        {
            var summary = new RunSummary();
            void Log(string message)
            {
                lock (summary.Log) summary.Log.Add(DateTime.UtcNow.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " " + message);
                observer.Log(message);
            }
            GameConnection game = null;
            Exception gameError = null;
            try {
                foreach (var partition in plan.Partitions) {
                    if (cancellationToken.IsCancellationRequested) {
                        summary.Problems.Add("Canceled before " + partition.Name + ".");
                        break;
                    }
                    var settings = Copy(options.Settings, plan, Path.Combine(options.ReportDirectory, partition.Mode + "-" + partition.Kind));
                    if (partition.Mode == Welcome.InGame && game == null && gameError == null) {
                        try {
                            game = await GameConnection.ConnectAsync(options.Connection, options.InstallRoot, options.ProcessId,
                                TimeSpan.FromSeconds(settings.StartupTimeoutSeconds), options.LaunchInBackground, Log, cancellationToken).ConfigureAwait(false);
                            Log("In-game partitions use process " + game.Host.ProcessId + " (" + game.Mode + (game.Owned ? ", started by this run" : "") + ").");
                        }
                        catch (Exception error) when (!(error is OperationCanceledException)) { gameError = error; }
                    }
                    if (partition.Mode == Welcome.InGame && gameError != null) {
                        // An unavailable game never shrinks the scope: the selected partition fails.
                        var failed = new PartitionResult { Partition = partition, Started = DateTime.UtcNow, Ended = DateTime.UtcNow };
                        failed.Problems.Add(gameError.Message);
                        summary.Partitions.Add(failed);
                        continue;
                    }
                    summary.Partitions.Add(await PartitionRunner.RunAsync(partition, settings, game, observer, cancellationToken).ConfigureAwait(false));
                }
            }
            finally {
                if (game != null) {
                    try { await game.ShutdownOwnedAsync(Log).ConfigureAwait(false); }
                    catch (Exception error) { summary.Problems.Add("The game started by this run could not be closed: " + error.Message); }
                }
            }
            // A failed partition already explains why nothing ran there.
            if (!options.Settings.ListOnly && summary.Partitions.All(partition => partition.Problems.Count == 0)) {
                var selected = summary.Partitions.Sum(partition => partition.Selected.Count);
                if (selected == 0) summary.Problems.Add("Zero tests: no case or fuzz target matched the selection in any partition.");
                else if (summary.Failed == 0 && summary.Passed < options.MinimumPassed)
                    summary.Problems.Add("Too few passed tests: " + summary.Passed + " passed, at least " + options.MinimumPassed + " required (skipped tests do not count).");
            }
            summary.ReportFile = Path.Combine(options.ReportDirectory, "wotr-run.json");
            Directory.CreateDirectory(options.ReportDirectory);
            File.WriteAllText(summary.ReportFile, JsonText.Write(new Dictionary<string, object> {
                ["runId"] = options.Settings.RunId,
                ["testAssembly"] = Path.GetFullPath(options.Settings.TestAssembly),
                ["requested"] = new Dictionary<string, object> { ["mode"] = plan.RequestedMode, ["kind"] = plan.RequestedKind, ["fuzzSeed"] = plan.FuzzSeed,
                    ["replayTarget"] = plan.Replay?.Target },
                ["partitions"] = summary.Partitions.Select(partition => new Dictionary<string, object> {
                    ["name"] = partition.Partition.Name, ["report"] = partition.ReportFile, ["selected"] = partition.Selected.Count,
                    ["passed"] = partition.Passed, ["failed"] = partition.Failed, ["skipped"] = partition.Skipped, ["succeeded"] = partition.Succeeded,
                    ["problems"] = partition.Problems
                }).ToList(),
                ["excluded"] = plan.Excluded.Select(partition => new Dictionary<string, object> { ["name"] = partition.Name, ["declared"] = partition.Declared }).ToList(),
                // Their results are in VSTest's own reports (TRX, environment.json), not in partition reports here.
                ["runByVSTest"] = plan.RunByVSTest.Select(partition => new Dictionary<string, object> { ["name"] = partition.Name, ["declared"] = partition.Declared }).ToList(),
                ["problems"] = summary.Problems,
                ["log"] = summary.Log,
                ["succeeded"] = summary.Succeeded
            }));
            return summary;
        }

        private static PartitionSettings Copy(PartitionSettings source, ExecutionPlan plan, string reportDirectory) => new PartitionSettings {
            TestAssembly = source.TestAssembly, ReportDirectory = reportDirectory, RunId = source.RunId, TestTimeoutSeconds = source.TestTimeoutSeconds,
            StartupTimeoutSeconds = source.StartupTimeoutSeconds, ListOnly = source.ListOnly, FuzzSeed = plan.FuzzSeed, Replay = plan.Replay,
            InputRoot = source.InputRoot, Runtime = source.Runtime, MonoHost = source.MonoHost, WorkDirectory = source.WorkDirectory, ClassLibrary = source.ClassLibrary,
            ExpectedModules = source.ExpectedModules.ToList(), SupportFiles = source.SupportFiles.ToList()
        };
    }
}
