using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using WotR.Testing.Client;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Build
{
    public sealed class PrepareTestRun : Task
    {
        [Required] public string WorkDirectory { get; set; }
        [Required] public string InputRoot { get; set; }
        [Required] public string MonoHost { get; set; }
        [Required] public string RunnerDirectory { get; set; }
        [Required] public string Runtime { get; set; }
        [Required] public string OriginalConsole { get; set; }
        [Required] public string BootstrapDirectory { get; set; }
        [Required] public string SourceDirectory { get; set; }
        public string OriginalSettings { get; set; }
        public string OriginalAdapterPath { get; set; }
        public string ReportDirectory { get; set; }
        public int TimeoutSeconds { get; set; } = 900;
        /// <summary>The WotR run settings of this invocation; when set, the run is planned across environments and workloads.</summary>
        public string RunSettingsFile { get; set; }
        [Output] public string PreparedSettings { get; set; }
        [Output] public string AdapterPath { get; set; }
        [Output] public string RunReport { get; set; }
        [Output] public string EnvironmentDirectory { get; set; }
        [Output] public string PreparedConsole { get; set; }
        /// <summary>Whether the ordinary offgame tests run; false drops the test assembly from the VSTest sources.</summary>
        [Output] public bool OffgameTests { get; set; } = true;
        /// <summary>The .wotrtests source for the other partitions, if any.</summary>
        [Output] public string Manifest { get; set; }

        public override bool Execute()
        {
            try {
                if (Runtime != "mono" && Runtime != "netfx") throw new ArgumentException("WotrRuntime must be mono or netfx.");
                if (TimeoutSeconds < 1) throw new ArgumentException("WotrTestTimeoutSeconds must be positive.");
                var directory = Path.Combine(Path.GetFullPath(WorkDirectory), "test-runs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                PreparedSettings = Path.Combine(directory, "wotr.runsettings");
                RunReport = Path.Combine(directory, "wotr-results.xml");
                // Every invocation gets a fresh directory; a previous report cannot make a failed run look successful.
                EnvironmentDirectory = string.IsNullOrEmpty(ReportDirectory) ? Path.Combine(directory, "reports")
                    : Path.Combine(Path.GetFullPath(ReportDirectory), Path.GetFileName(directory));
                var document = string.IsNullOrEmpty(OriginalSettings) ? new XDocument(new XElement("RunSettings")) : XDocument.Load(OriginalSettings);
                var root = document.Root;
                if (root?.Name != "RunSettings") throw new ArgumentException("Expected a RunSettings root element.");
                var config = Child(root, "RunConfiguration");
                config.SetElementValue("TreatNoTestsAsError", "true");
                if (config.Element("TestSessionTimeout") == null) config.SetElementValue("TestSessionTimeout", checked(TimeoutSeconds * 1000));
                if (Runtime == "mono") {
                    config.SetElementValue("DisableAppDomain", "true");
                    config.SetElementValue("TargetPlatform", "x64");
                    // xUnit must execute in the Mono host's domain, where the generated resolver is installed.
                    Child(root, "xUnit").SetElementValue("AppDomain", "denied");
                    Child(root, "xUnit").SetElementValue("ShadowCopy", "false");
                }
                Child(config, "EnvironmentVariables").SetElementValue("WOTR_OFFLINE_REPORT_DIR", EnvironmentDirectory);
                root.Element("WotR")?.Remove();
                root.Add(new XElement("WotR", new XElement("Runtime", Runtime), new XElement("InputRoot", Path.GetFullPath(InputRoot)),
                    new XElement("WorkDirectory", Path.GetFullPath(WorkDirectory)), new XElement("MonoHost", Path.GetFullPath(MonoHost))));
                var sources = Plan(directory);
                var provider = Path.Combine(Path.GetFullPath(RunnerDirectory), "WotR.Testing.MonoRuntimeProvider.dll");
                PreparedConsole = Path.Combine(directory, "WotR.Testing.VSTestBootstrap.dll");
                File.Copy(Path.Combine(BootstrapDirectory, "WotR.Testing.VSTestBootstrap.dll"), PreparedConsole);
                File.Copy(Path.Combine(BootstrapDirectory, "WotR.Testing.VSTestBootstrap.runtimeconfig.json"), Path.ChangeExtension(PreparedConsole, ".runtimeconfig.json"));
                new XDocument(new XElement("Bootstrap", new XElement("Console", Path.GetFullPath(OriginalConsole)),
                    new XElement("Provider", provider), new XElement("SourceDirectory", Path.GetFullPath(SourceDirectory)), new XElement("PreparedSettings", PreparedSettings),
                    new XElement("SourceSettings", string.IsNullOrEmpty(OriginalSettings) ? PreparedSettings : Path.GetFullPath(OriginalSettings)), sources))
                    .Save(Path.Combine(directory, "bootstrap.xml"));
                var loggers = Child(Child(root, "LoggerRunSettings"), "Loggers");
                foreach (var previous in loggers.Elements("Logger").Where(e => (string)e.Attribute("friendlyName") == "wotr").ToArray()) previous.Remove();
                loggers.Add(new XElement("Logger", new XAttribute("friendlyName", "wotr"), new XAttribute("enabled", "true"),
                    new XAttribute("assemblyQualifiedName", "WotR.Testing.VSTest.RunLogger, " + System.Reflection.AssemblyName.GetAssemblyName(provider).FullName),
                    new XAttribute("codeBase", provider), new XElement("Configuration", new XElement("ReportFile", RunReport))));
                document.Save(PreparedSettings);
                AdapterPath = string.Join(";", new[] { OriginalAdapterPath, Path.GetFullPath(RunnerDirectory) }.Where(p => !string.IsNullOrEmpty(p)));
                return true;
            }
            catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
        }

        /// <summary>
        /// Plans the run from the test assembly's metadata. Ordinary offgame tests keep the existing VSTest path; every
        /// other partition goes into a .wotrtests manifest for the WotR adapter. Returns the bootstrap's source changes.
        /// </summary>
        private XElement Plan(string directory)
        {
            var changes = new XElement("Sources");
            if (string.IsNullOrEmpty(RunSettingsFile)) return changes;
            var settings = RunSettings.Read(RunSettingsFile);
            var scope = TestScope.Scan(settings.TestAssembly);
            var plan = ExecutionPlan.Resolve(scope, settings.Mode, settings.Kind, settings.FuzzSeed, settings.FuzzReplay, Runtime, settings.InGameOptionsGiven);
            bool IsOffgameTests(Partition partition) => partition.Mode == Welcome.OffGame && partition.Kind == TestDescriptor.TestKind;
            OffgameTests = plan.Partitions.Any(IsOffgameTests);
            changes.Add(new XElement("TestAssembly", Path.GetFullPath(settings.TestAssembly)), new XElement("Keep", OffgameTests));
            // The ordinary xUnit adapter must leave cases declared only for the game alone.
            if (OffgameTests && scope.Count(Welcome.InGame, TestDescriptor.TestKind) > 0) changes.Add(new XElement("Filter", TestDescriptor.ModeTrait + "!=" + Welcome.InGame));
            var others = plan.Partitions.Where(partition => !IsOffgameTests(partition)).ToList();
            foreach (var partition in plan.Partitions) Log.LogMessage(MessageImportance.High, "WotR partition: " + partition.Name + " (" + partition.Declared + " declared)");
            if (others.Count == 0) return changes;
            var inGame = others.Any(partition => partition.Mode == Welcome.InGame);
            var manifest = new RunManifest {
                Settings = settings, RequestedMode = plan.RequestedMode, RequestedKind = plan.RequestedKind, Replay = plan.Replay,
                Partitions = others.Select(partition => new ManifestPartition { Mode = partition.Mode, Kind = partition.Kind, Declared = partition.Declared }).ToList(),
                ReportDirectory = EnvironmentDirectory, RunId = Path.GetFileName(directory),
                // Everything that needs assembly metadata is resolved here, outside the .NET Framework test host.
                ClassLibrary = Runtime == "mono" && others.Any(partition => partition.Mode == Welcome.OffGame) ? OffgameWorker.PrepareClassLibrary(settings.InputRoot, settings.WorkDirectory) : null,
                SupportFiles = inGame ? PlayerFacades.Find(settings.InstallRoot).ToList() : new System.Collections.Generic.List<string>(),
                ExpectedModules = inGame ? ModuleIdentities.Read(settings.ModAssemblies) : new System.Collections.Generic.List<ModuleIdentity>()
            };
            manifest.Settings.Runtime = Runtime;
            Manifest = Path.Combine(directory, Path.GetFileNameWithoutExtension(settings.TestAssembly) + RunManifest.Extension);
            manifest.Write(Manifest);
            changes.Add(new XElement("Add", Manifest));
            return changes;
        }

        private static XElement Child(XElement parent, string name)
        {
            var element = parent.Element(name);
            if (element == null) { element = new XElement(name); parent.Add(element); }
            return element;
        }
    }
}
