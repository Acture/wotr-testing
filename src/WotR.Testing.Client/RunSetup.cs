using System;
using System.IO;
using System.Linq;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>Resolves settings and command-line choices into a plan and run options, the same way for both front ends.</summary>
    public static class RunSetup
    {
        public static (ExecutionPlan Plan, RunOptions Options) Create(RunSettings settings, string mode, string kind, int? fuzzSeed,
            string fuzzReplay, bool listOnly, string runId)
        {
            if (settings.TestAssembly == null || !File.Exists(settings.TestAssembly))
                throw new PlanException("The test assembly was not found: " + settings.TestAssembly + ". Build the project first.");
            // Explicit option > project property > built-in default.
            mode = !string.IsNullOrEmpty(mode) ? mode : settings.Mode;
            kind = !string.IsNullOrEmpty(kind) ? kind : settings.Kind;
            fuzzSeed = fuzzSeed ?? settings.FuzzSeed;
            fuzzReplay = !string.IsNullOrEmpty(fuzzReplay) ? fuzzReplay : settings.FuzzReplay;
            var runtime = string.IsNullOrEmpty(settings.Runtime) ? "mono" : settings.Runtime;
            var plan = ExecutionPlan.Resolve(TestScope.Scan(settings.TestAssembly), mode, kind, fuzzSeed, fuzzReplay, runtime, settings.InGameOptionsGiven);
            var inGame = plan.Partitions.Any(partition => partition.Mode == Welcome.InGame);
            if (inGame && (string.IsNullOrEmpty(settings.InstallRoot) || !File.Exists(Path.Combine(settings.InstallRoot, GameConnection.GameExecutable))))
                throw new PlanException("In-game partitions need the game installation; set WrathInstallDir (" + settings.InstallRoot + " has no " + GameConnection.GameExecutable + ").");
            var reportRoot = Path.Combine(Path.GetFullPath(settings.ReportDirectory), runId);
            var partition = new PartitionSettings {
                TestAssembly = Path.GetFullPath(settings.TestAssembly), RunId = runId, ListOnly = listOnly,
                TestTimeoutSeconds = settings.TestTimeoutSeconds > 0 ? settings.TestTimeoutSeconds : 60,
                StartupTimeoutSeconds = settings.StartupTimeoutSeconds > 0 ? settings.StartupTimeoutSeconds : 300,
                InputRoot = settings.InputRoot, Runtime = runtime, MonoHost = settings.MonoHost,
                WorkDirectory = string.IsNullOrEmpty(settings.WorkDirectory) ? Path.Combine(Path.GetTempPath(), "wotr-testing") : settings.WorkDirectory,
                ExpectedModules = inGame ? ModuleIdentities.Read(settings.ModAssemblies) : new System.Collections.Generic.List<ModuleIdentity>(),
                SupportFiles = inGame ? PlayerFacades.Find(settings.InstallRoot).ToList() : new System.Collections.Generic.List<string>()
            };
            return (plan, new RunOptions {
                Settings = partition, ReportDirectory = reportRoot,
                Connection = string.IsNullOrEmpty(settings.Connection) ? ConnectionModes.Auto : settings.Connection,
                ProcessId = settings.ProcessId, InstallRoot = settings.InstallRoot,
                MinimumPassed = settings.MinimumPassed > 0 ? settings.MinimumPassed : 1,
                LaunchInBackground = settings.LaunchInBackground ?? true
            });
        }

        public static string NewRunId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    }
}
