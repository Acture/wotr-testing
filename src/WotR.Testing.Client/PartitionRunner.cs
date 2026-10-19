using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>Everything one partition needs; the front ends fill it from MSBuild properties or command-line options.</summary>
    public sealed class PartitionSettings
    {
        public string TestAssembly { get; set; }
        public string ReportDirectory { get; set; }
        public string RunId { get; set; }
        public int TestTimeoutSeconds { get; set; } = 60;
        public int StartupTimeoutSeconds { get; set; } = 300;
        public bool ListOnly { get; set; }
        public int? FuzzSeed { get; set; }
        public ReplaySelection Replay { get; set; }

        // Offgame worker.
        public string InputRoot { get; set; }
        public string Runtime { get; set; } = "mono";
        public string MonoHost { get; set; }
        public string WorkDirectory { get; set; }

        // In-game host.
        public List<ModuleIdentity> ExpectedModules { get; set; } = new List<ModuleIdentity>();
        public List<string> SupportFiles { get; set; } = new List<string>();
    }

    /// <summary>Front-end callbacks: selection (filters) and results as they arrive.</summary>
    public interface IPartitionObserver
    {
        bool Select(Partition partition, TestDescriptor test);
        void Started(Partition partition, TestDescriptor test, TestStarted started);
        void Finished(Partition partition, TestDescriptor test, TestFinished finished);
        void Log(string message);
    }

    public sealed class PartitionResult
    {
        public Partition Partition { get; set; }
        public List<TestDescriptor> Discovered { get; } = new List<TestDescriptor>();
        public List<TestDescriptor> Selected { get; } = new List<TestDescriptor>();
        public IReadOnlyList<TestFinished> Results { get; set; } = new List<TestFinished>();
        public SessionFinished Final { get; set; }
        public Welcome Host { get; set; }
        public List<string> Problems { get; } = new List<string>();
        public string Connection { get; set; }
        public bool Owned { get; set; }
        public DateTime Started { get; set; }
        public DateTime Ended { get; set; }
        public string ReportFile { get; set; }

        public int Passed => Results.Count(result => result.Outcome == TestFinished.Passed);
        public int Failed => Results.Count(result => result.Outcome == TestFinished.Failed);
        public int Skipped => Results.Count(result => result.Outcome == TestFinished.Skipped);
        public bool Succeeded => Problems.Count == 0 && Failed == 0;
    }

    /// <summary>Runs one partition over the protocol and checks the session; never reuses a report from an earlier run.</summary>
    public static class PartitionRunner
    {
        public static async Task<PartitionResult> RunAsync(Partition partition, PartitionSettings settings, GameConnection game,
            IPartitionObserver observer, CancellationToken cancellationToken)
        {
            var result = new PartitionResult { Partition = partition, Started = DateTime.UtcNow };
            Directory.CreateDirectory(settings.ReportDirectory);
            OffgameWorker worker = null;
            HostClient client = null;
            try {
                if (partition.Mode == Welcome.OffGame) {
                    worker = await OffgameWorker.StartAsync(settings.Runtime, settings.InputRoot, settings.MonoHost, settings.WorkDirectory, settings.TestAssembly,
                        settings.ReportDirectory, settings.RunId, observer.Log, cancellationToken).ConfigureAwait(false);
                    client = worker.Client;
                    result.Connection = "worker";
                }
                else {
                    client = await game.OpenSessionAsync(settings.RunId, cancellationToken).ConfigureAwait(false);
                    result.Connection = game.Mode;
                    result.Owned = game.Owned;
                }
                result.Host = client.Host;
                if (client.Host.Mode != partition.Mode)
                    throw new InvalidOperationException("Mode mismatch: the " + partition.Mode + " partition reached a host that runs " + client.Host.Mode + ".");
                client.Received += message => { if (message is Log log) observer.Log("[" + partition.Name + "] " + log.Text); };
                var lease = await client.LeaseAsync(new Lease {
                    TestAssembly = Path.GetFullPath(settings.TestAssembly), BundleHash = Sha256(settings.TestAssembly), Kind = partition.Kind,
                    ExpectedModules = partition.Mode == Welcome.InGame ? settings.ExpectedModules : new List<ModuleIdentity>(),
                    SupportFiles = partition.Mode == Welcome.InGame ? settings.SupportFiles : new List<string>(),
                    ReportDirectory = settings.ReportDirectory, TestTimeoutSeconds = settings.TestTimeoutSeconds,
                    ReadyTimeoutSeconds = settings.StartupTimeoutSeconds
                }, cancellationToken).ConfigureAwait(false);
                if (!lease.Granted) throw new InvalidOperationException("[" + lease.Reason + "] " + lease.Detail);
                var all = await client.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                result.Discovered.AddRange(all.Where(test => test.Kind == partition.Kind && test.RunsIn(partition.Mode)));
                var byId = result.Discovered.ToDictionary(test => test.Id, StringComparer.Ordinal);
                if (settings.Replay != null) {
                    var target = result.Discovered.FirstOrDefault(test => test.Id == settings.Replay.Target);
                    if (target == null) throw new InvalidOperationException("The replay target " + settings.Replay.Target + " is not a registered " + partition.Mode + " fuzz target.");
                    result.Selected.Add(target);
                }
                else result.Selected.AddRange(result.Discovered.Where(test => observer.Select(partition, test)));
                client.Received += message => {
                    if (message is TestStarted started && byId.TryGetValue(started.TestId, out var test)) observer.Started(partition, test, started);
                    else if (message is TestFinished finished && byId.TryGetValue(finished.TestId, out test)) {
                        if (finished.ReplayCase != null && finished.Outcome == TestFinished.Failed && settings.Replay == null)
                            finished.Message += "\n" + SaveReplay(settings, partition, finished);
                        observer.Finished(partition, test, finished);
                    }
                };
                var ids = settings.ListOnly ? new List<string>() : result.Selected.Select(test => test.Id).ToList();
                result.Final = await client.RunAsync(new Run { TestIds = ids, FuzzSeed = settings.FuzzSeed, FuzzReplayCase = settings.Replay?.Case }, cancellationToken).ConfigureAwait(false);
                result.Results = client.Tracker.Results;
                result.Problems.AddRange(client.Tracker.Problems());
                var preparation = result.Final.Environment?.IndexOf("\"offlinePreparationError\":\"", StringComparison.Ordinal) ?? -1;
                if (preparation >= 0) result.Problems.Add("[INIT_FAILED] The offline runtime could not be prepared; see offlinePreparationError in " +
                    Path.Combine(settings.ReportDirectory, "partition.json") + ".");
                if (!settings.ListOnly && result.Selected.Count > 0) {
                    if (result.Results.Count == 0) result.Problems.Add("No case of the selected " + partition.Name + " partition ran.");
                    else if (result.Results.All(test => test.Outcome == TestFinished.Skipped)) result.Problems.Add("Every case of the " + partition.Name + " partition was skipped.");
                    var missing = result.Selected.Where(test => !result.Results.Any(item => item.TestId == test.Id)).ToList();
                    if (missing.Count > 0) result.Problems.Add(missing.Count + " selected cases reported no result: " + string.Join(", ", missing.Take(5).Select(test => test.DisplayName)));
                }
                if (partition.Mode == Welcome.OffGame && !settings.ListOnly) CheckOfflineEnvironment(settings, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                result.Problems.Add("The run was canceled.");
            }
            catch (Exception error) {
                result.Problems.Add(error is ConnectionException || error is ProtocolException || error is InvalidOperationException ? error.Message : error.ToString());
                if (worker != null && worker.Errors.Length > 0) result.Problems.Add("Worker output:\n" + worker.Errors);
                if (client != null) result.Results = client.Tracker.Results;
            }
            finally {
                worker?.Dispose();
                if (worker == null) client?.Dispose();
                result.Ended = DateTime.UtcNow;
                result.ReportFile = WriteReport(settings, result);
            }
            return result;
        }

        /// <summary>Writes a fuzz finding as a replay file (the format of FuzzReplayFile) and says how to replay it.</summary>
        private static string SaveReplay(PartitionSettings settings, Partition partition, TestFinished finished)
        {
            try {
                var directory = Path.Combine(settings.ReportDirectory, "replays");
                Directory.CreateDirectory(directory);
                var name = new string(finished.TestId.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_').ToArray());
                var path = Path.Combine(directory, (name.Length > 120 ? name.Substring(0, 120) : name) + ".json");
                File.WriteAllText(path, JsonText.Write(new Dictionary<string, object> {
                    ["FormatVersion"] = 1, ["Target"] = finished.TestId, ["Mode"] = partition.Mode, ["Case"] = finished.ReplayCase
                }));
                return "Saved replay case: " + path + "\nReplay it exactly with: dotnet test --fuzz-replay \"" + path + "\" (or -p:WotrFuzzReplay=\"" + path + "\")";
            }
            catch (Exception error) { return "The replay case could not be saved: " + error.Message; }
        }

        /// <summary>Offline tests write environment.json through WotrGameFixture; it must exist and match the runtime.</summary>
        private static void CheckOfflineEnvironment(PartitionSettings settings, PartitionResult result)
        {
            var usesOffline = File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.TestAssembly)), "WotR.Testing.Offline.dll"));
            var environment = Path.Combine(settings.ReportDirectory, "environment.json");
            if (!File.Exists(environment)) {
                if (usesOffline && result.Partition.Kind == TestDescriptor.TestKind && result.Results.Count > 0)
                    result.Problems.Add("Missing report: environment.json was not written by the offline fixture.");
                return;
            }
            var text = File.ReadAllText(environment);
            var expected = settings.Runtime == "mono" ? "\"unity-mono\"" : "\"netfx\"";
            if (!text.Contains("\"runtimeKind\": " + expected) && !text.Contains("\"runtimeKind\":" + expected))
                result.Problems.Add("Runtime mismatch: environment.json does not report runtimeKind " + expected + ".");
        }

        private static string WriteReport(PartitionSettings settings, PartitionResult result)
        {
            var path = Path.Combine(settings.ReportDirectory, "partition.json");
            try {
                File.WriteAllText(path, JsonText.Write(Report(result)));
                return path;
            }
            catch (Exception error) {
                result.Problems.Add("The partition report could not be written: " + error.Message);
                return null;
            }
        }

        public static Dictionary<string, object> Report(PartitionResult result) => new Dictionary<string, object> {
            ["mode"] = result.Partition.Mode,
            ["kind"] = result.Partition.Kind,
            ["declared"] = result.Partition.Declared,
            ["started"] = result.Started,
            ["ended"] = result.Ended,
            ["connection"] = new Dictionary<string, object> {
                ["kind"] = result.Connection, ["processId"] = result.Host?.ProcessId, ["owned"] = result.Owned,
                ["processStartTime"] = result.Host?.ProcessStartTime
            },
            ["process"] = result.Host == null ? null : new Dictionary<string, object> {
                ["runtimeKind"] = result.Host.RuntimeKind, ["hostVersion"] = result.Host.HostVersion, ["gameVersion"] = result.Host.GameVersion,
                ["unityVersion"] = result.Host.UnityVersion, ["installRoot"] = result.Host.InstallRoot, ["mainThreadId"] = result.Host.MainThreadId,
                ["readiness"] = result.Host.Readiness
            },
            ["counts"] = new Dictionary<string, object> {
                ["discovered"] = result.Discovered.Count, ["selected"] = result.Selected.Count, ["passed"] = result.Passed,
                ["failed"] = result.Failed, ["skipped"] = result.Skipped
            },
            ["cleanup"] = result.Final?.Cleanup,
            ["complete"] = result.Final != null,
            ["finalSequence"] = result.Final?.Sequence,
            ["host"] = new JsonText.Raw(result.Final?.Environment),
            ["problems"] = result.Problems,
            ["succeeded"] = result.Succeeded
        };

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
