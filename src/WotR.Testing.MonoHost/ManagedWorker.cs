using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using WotR.Testing.InGame.Protocol;
using WotR.Testing.Runner;

namespace WotR.Testing.MonoHost
{
    /// <summary>
    /// Offgame worker for the WotR test adapters: serves one protocol session for one test assembly, then exits.
    /// Usage: --worker --endpoint-file &lt;path&gt; --token &lt;secret&gt; --source &lt;test.dll&gt; [--parent &lt;pid&gt;]. The worker
    /// listens on a loopback port and writes it to the endpoint file.
    /// </summary>
    internal static class ManagedWorker
    {
        internal static int Run(string[] args)
        {
            try {
                var options = new Dictionary<string, string>();
                for (int index = 0; index + 1 < args.Length; index += 2) options[args[index]] = args[index + 1];
                string Required(string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException("missing " + name);
                var source = Path.GetFullPath(Required("--source"));
                if (!File.Exists(source)) throw new FileNotFoundException("The test assembly was not found.", source);
                var setup = new AppDomainSetup {
                    ApplicationBase = Path.GetDirectoryName(source),
                    ConfigurationFile = File.Exists(source + ".config") ? source + ".config" : null,
                    ShadowCopyFiles = "false"
                };
                var domain = AppDomain.CreateDomain("WotRTestWorker", null, setup);
                // Set before test code loads, as the in-game host does for its mode.
                domain.SetData(RunnerEnvironment.ModeKey, Welcome.OffGame);
                var worker = (ManagedWorkerDomain)domain.CreateInstanceFromAndUnwrap(typeof(ManagedWorkerDomain).Assembly.Location, typeof(ManagedWorkerDomain).FullName);
                return worker.Serve(Required("--endpoint-file"), Required("--token"), source, options.TryGetValue("--parent", out var parent) ? int.Parse(parent) : 0);
            }
            catch (Exception error) {
                Console.Error.WriteLine("wotr-mono-host: worker failed: " + error);
                return 2;
            }
        }
    }

    /// <summary>Runs in the test assembly's domain.</summary>
    public sealed class ManagedWorkerDomain : MarshalByRefObject
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

        public override object InitializeLifetimeService() => null;

        public int Serve(string endpointFile, string token, string source, int parent)
        {
            var toolsDirectory = Path.GetDirectoryName(typeof(ManagedWorkerDomain).Assembly.Location);
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
                var path = Path.Combine(toolsDirectory, new AssemblyName(request.Name).Name + ".dll");
                return Path.GetFileName(path).StartsWith("WotR.Testing.", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            // As in the VSTest path: reflection may touch game types before any module initializer runs.
            var offlinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WotR.Testing.Offline.dll");
            if (File.Exists(offlinePath)) {
                var install = Assembly.LoadFrom(offlinePath).GetType("WotR.Testing.Offline.OfflineRuntime", true)
                    .GetMethod("InstallResolver", BindingFlags.Public | BindingFlags.Static);
                try { install.Invoke(null, null); }
                catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
            }
            return Session.Serve(endpointFile, token, source, parent, ConnectTimeout);
        }

        // A separate class keeps Runner and protocol types out of Serve until the resolver above is installed.
        private static class Session
        {
            internal static int Serve(string endpointFile, string token, string source, int parent, TimeSpan connectTimeout)
            {
                var environment = new WorkerEnvironment(source);
                using (var process = Process.GetCurrentProcess()) {
                    var advertisement = new HostAdvertisement {
                        ProtocolVersion = Message.CurrentVersion, ProcessId = process.Id, ProcessStartTime = HostAdvertisement.Format(process.StartTime),
                        Token = token, HostVersion = RunnerEnvironment.HostVersion, Mode = Welcome.OffGame
                    };
                    using (var server = new RunnerServer(environment, advertisement)) {
                        server.Log += message => Console.Error.WriteLine("wotr-worker: " + message);
                        server.Start();
                        var temporary = endpointFile + ".tmp";
                        File.WriteAllText(temporary, advertisement.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        File.Move(temporary, endpointFile);
                        // One session: exit when it ends, when nobody connects, or when the adapter that started us exits.
                        var deadline = DateTime.UtcNow + connectTimeout;
                        while (!environment.Ended.Wait(500)) {
                            if (!environment.Started && DateTime.UtcNow > deadline) {
                                Console.Error.WriteLine("wotr-worker: no adapter connected within " + connectTimeout.TotalSeconds + " seconds.");
                                return 3;
                            }
                            if (parent != 0 && HostAdvertisement.StartTimeOf(parent) == null) return 4;
                        }
                    }
                }
                return 0;
            }
        }

        private sealed class WorkerEnvironment : RunnerEnvironment
        {
            private readonly string source;
            private Lease lease;

            internal WorkerEnvironment(string source) => this.source = source;

            internal readonly ManualResetEventSlim Ended = new ManualResetEventSlim();
            internal bool Started;

            public override string Mode => Welcome.OffGame;
            public override string RuntimeKind => Type.GetType("Mono.Runtime") != null ? "unity-mono" : "netfx";
            public override int MainThreadId => 0;

            public override Assembly LoadTestAssembly(Lease request, out LeaseResult refusal)
            {
                refusal = null;
                if (!string.Equals(Path.GetFullPath(request.TestAssembly ?? ""), source, StringComparison.OrdinalIgnoreCase)) {
                    refusal = new LeaseResult { Reason = LeaseResult.Invalid, Detail = "This worker was started for " + source + "." };
                    return null;
                }
                lease = request;
                // The offline fixture writes environment.json here.
                if (!string.IsNullOrEmpty(request.ReportDirectory))
                    Environment.SetEnvironmentVariable("WOTR_OFFLINE_REPORT_DIR", request.ReportDirectory);
                return Assembly.LoadFrom(source);
            }

            public override void SessionStarting(CancellationToken canceled) => Started = true;
            public override void SessionEnded() => Ended.Set();

            public override string EnvironmentReport(Lease request) => ProtocolJson.Serialize(new WorkerReport {
                Mode = Mode, RuntimeKind = RuntimeKind, ProcessId = Process.GetCurrentProcess().Id, TestAssembly = source,
                ReportDirectory = request?.ReportDirectory, Dirty = Dirty, OfflinePreparationError = OfflinePreparationError()
            });

            // Read by name: the offline library is the test project's copy, not a reference of this host.
            private static string OfflinePreparationError()
            {
                var offline = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "WotR.Testing.Offline");
                var error = offline?.GetType("WotR.Testing.Offline.OfflineRuntime")?.GetProperty("PreparationError")?.GetValue(null) as Exception;
                return error?.ToString();
            }
        }

        [DataContract]
        private sealed class WorkerReport
        {
            [DataMember(Name = "mode", Order = 0)] public string Mode { get; set; }
            [DataMember(Name = "runtimeKind", Order = 1)] public string RuntimeKind { get; set; }
            [DataMember(Name = "processId", Order = 2)] public int ProcessId { get; set; }
            [DataMember(Name = "testAssembly", Order = 3)] public string TestAssembly { get; set; }
            [DataMember(Name = "reportDirectory", Order = 4)] public string ReportDirectory { get; set; }
            [DataMember(Name = "dirty", Order = 5)] public string Dirty { get; set; }
            [DataMember(Name = "offlinePreparationError", Order = 6)] public string OfflinePreparationError { get; set; }
        }
    }
}
