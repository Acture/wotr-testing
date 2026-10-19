using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>
    /// The VSTest source for partitions the ordinary xUnit adapter does not run (in-game cases and fuzz targets). The
    /// build task resolves everything that needs assembly metadata, so the adapter in the .NET Framework test host only
    /// reads this file.
    /// </summary>
    [DataContract]
    public sealed class RunManifest
    {
        public const string Extension = ".wotrtests";

        [DataMember(Name = "formatVersion", Order = 0)] public int FormatVersion { get; set; } = 1;
        [DataMember(Name = "settings", Order = 1)] public RunSettings Settings { get; set; }
        [DataMember(Name = "requestedMode", Order = 2)] public string RequestedMode { get; set; }
        [DataMember(Name = "requestedKind", Order = 3)] public string RequestedKind { get; set; }
        [DataMember(Name = "partitions", Order = 4)] public List<ManifestPartition> Partitions { get; set; }
        [DataMember(Name = "replay", Order = 5)] public ReplaySelection Replay { get; set; }
        [DataMember(Name = "reportDirectory", Order = 6)] public string ReportDirectory { get; set; }
        [DataMember(Name = "runId", Order = 7)] public string RunId { get; set; }
        [DataMember(Name = "classLibrary", Order = 8)] public string ClassLibrary { get; set; }
        [DataMember(Name = "supportFiles", Order = 9)] public List<string> SupportFiles { get; set; }
        [DataMember(Name = "expectedModules", Order = 10)] public List<ModuleIdentity> ExpectedModules { get; set; }

        public static RunManifest Read(string path) => ProtocolJson.Deserialize<RunManifest>(File.ReadAllText(path));

        public void Write(string path) => File.WriteAllText(path, ProtocolJson.Serialize(this));

        public (ExecutionPlan Plan, RunOptions Options) Resolve(bool listOnly)
        {
            var plan = ExecutionPlan.FromPartitions(RequestedMode, RequestedKind,
                Partitions.Select(item => new Partition(item.Mode, item.Kind, item.Declared)).ToList(), Settings.FuzzSeed, Replay);
            var partition = new PartitionSettings {
                TestAssembly = Settings.TestAssembly, RunId = RunId, ListOnly = listOnly,
                TestTimeoutSeconds = Settings.TestTimeoutSeconds > 0 ? Settings.TestTimeoutSeconds : 60,
                StartupTimeoutSeconds = Settings.StartupTimeoutSeconds > 0 ? Settings.StartupTimeoutSeconds : 300,
                InputRoot = Settings.InputRoot, Runtime = string.IsNullOrEmpty(Settings.Runtime) ? "mono" : Settings.Runtime,
                MonoHost = Settings.MonoHost, WorkDirectory = Settings.WorkDirectory, ClassLibrary = ClassLibrary,
                ExpectedModules = ExpectedModules ?? new List<ModuleIdentity>(), SupportFiles = SupportFiles ?? new List<string>()
            };
            return (plan, new RunOptions {
                Settings = partition, ReportDirectory = ReportDirectory,
                Connection = string.IsNullOrEmpty(Settings.Connection) ? ConnectionModes.Auto : Settings.Connection,
                ProcessId = Settings.ProcessId, InstallRoot = Settings.InstallRoot,
                // The aggregate minimum is checked over every source after the run.
                MinimumPassed = 0, LaunchInBackground = Settings.LaunchInBackground ?? true
            });
        }
    }

    [DataContract]
    public sealed class ManifestPartition
    {
        [DataMember(Name = "mode", Order = 0)] public string Mode { get; set; }
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; set; }
        [DataMember(Name = "declared", Order = 2)] public int Declared { get; set; }
    }
}
