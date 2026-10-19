using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>
    /// The project's WotR settings for one invocation, written by the build targets with the current MSBuild
    /// properties (including -p overrides, also with --no-build). Command-line options take precedence.
    /// </summary>
    [DataContract]
    public sealed class RunSettings
    {
        [DataMember(Name = "testAssembly", Order = 0)] public string TestAssembly { get; set; }
        [DataMember(Name = "inputRoot", Order = 1)] public string InputRoot { get; set; }
        [DataMember(Name = "installRoot", Order = 2)] public string InstallRoot { get; set; }
        [DataMember(Name = "monoHost", Order = 3)] public string MonoHost { get; set; }
        [DataMember(Name = "workDirectory", Order = 4)] public string WorkDirectory { get; set; }
        [DataMember(Name = "reportDirectory", Order = 5)] public string ReportDirectory { get; set; }
        [DataMember(Name = "runtime", Order = 6)] public string Runtime { get; set; }
        [DataMember(Name = "mode", Order = 7)] public string Mode { get; set; }
        [DataMember(Name = "kind", Order = 8)] public string Kind { get; set; }
        [DataMember(Name = "connection", Order = 9)] public string Connection { get; set; }
        [DataMember(Name = "processId", Order = 10)] public int ProcessId { get; set; }
        [DataMember(Name = "testTimeoutSeconds", Order = 11)] public int TestTimeoutSeconds { get; set; }
        [DataMember(Name = "startupTimeoutSeconds", Order = 12)] public int StartupTimeoutSeconds { get; set; }
        [DataMember(Name = "runTimeoutSeconds", Order = 13)] public int RunTimeoutSeconds { get; set; }
        [DataMember(Name = "minimumPassed", Order = 14)] public int MinimumPassed { get; set; }
        [DataMember(Name = "modAssemblies", Order = 15)] public List<string> ModAssemblies { get; set; }
        [DataMember(Name = "fuzzSeed", Order = 16)] public int? FuzzSeed { get; set; }
        [DataMember(Name = "fuzzReplay", Order = 17)] public string FuzzReplay { get; set; }
        /// <summary>True when the project set in-game connection options, which an offgame-only run rejects.</summary>
        [DataMember(Name = "inGameOptionsGiven", Order = 18)] public bool InGameOptionsGiven { get; set; }
        /// <summary>A game this run starts leaves the focus to the user while it loads; false lets it take the focus.</summary>
        [DataMember(Name = "launchInBackground", Order = 19)] public bool? LaunchInBackground { get; set; }

        public static RunSettings Read(string path) => ProtocolJson.Deserialize<RunSettings>(File.ReadAllText(path));

        public void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ProtocolJson.Serialize(this));
        }
    }
}
