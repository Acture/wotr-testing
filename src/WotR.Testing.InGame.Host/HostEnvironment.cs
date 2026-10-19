using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using Kingmaker;
using Kingmaker.EntitySystem.Persistence;
using UnityEngine;
using UnityModManagerNet;
using WotR.Testing.InGame.Protocol;
using WotR.Testing.Runner;

namespace WotR.Testing.InGame.Host
{
    /// <summary>
    /// The player as a test environment. Test assemblies bind to the game, Harmony and mod assemblies the player already
    /// loaded; only runner-owned files (the test assembly, xUnit and other test libraries) are copied and loaded.
    /// </summary>
    internal sealed class HostEnvironment : RunnerEnvironment
    {
        private readonly MainThreadDispatcher dispatcher;
        private readonly Dictionary<string, LoadedTests> tests = new Dictionary<string, LoadedTests>(StringComparer.OrdinalIgnoreCase);
        private readonly string managed;
        private readonly string gameVersion;
        private readonly string unityVersion;
        private readonly string installRoot;
        private volatile string readiness = "starting";
        private volatile string shadow;
        private CancellationTokenSource session;
        private Lease leased;
        private LoadedTests current;
        private bool? runInBackground;

        internal HostEnvironment(MainThreadDispatcher dispatcher, UnityModManager.ModEntry entry)
        {
            this.dispatcher = dispatcher;
            // Unity APIs are read once here, on the main thread during mod loading.
            unityVersion = Application.unityVersion;
            installRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            managed = Path.Combine(Application.dataPath, "Managed");
            try { gameVersion = Kingmaker.GameVersion.GetVersion(); }
            catch (Exception error) { entry.Logger.Warning("Game version unavailable: " + error.Message); }
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var directory = shadow;
                if (directory == null) return null;
                var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
        }

        public override string Mode => Welcome.InGame;
        public override string RuntimeKind => "unity-player";
        public override string GameVersion => gameVersion;
        public override string UnityVersion => unityVersion;
        public override string InstallRoot => installRoot;
        public override int MainThreadId => dispatcher.MainThreadId;
        public override long FrameCount => dispatcher.FrameCount;
        public override string Readiness => readiness;

        /// <summary>Main thread only.</summary>
        internal void RefreshReadiness()
        {
            try {
                if (!Game.HasInstance) readiness = "starting";
                else if (LoadingProcess.Instance != null && LoadingProcess.Instance.IsLoadingInProcess) readiness = "loading";
                else if (Game.Instance.CurrentlyLoadedArea is var area && area != null) readiness = "area:" + area.name;
                else readiness = "main-menu";
            }
            catch (Exception error) { readiness = "unknown: " + error.Message; }
        }

        /// <summary>Main-thread work needs the update loop, which UnityModManager starts once the game has loaded.</summary>
        public override string WaitUntilReady(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (dispatcher.MainThreadId == 0 || readiness == "starting" || readiness == "loading") {
                if (DateTime.UtcNow > deadline || cancellationToken.IsCancellationRequested)
                    return "The game did not finish loading within " + timeout.TotalSeconds + " seconds (state: " + readiness +
                        (dispatcher.MainThreadId == 0 ? ", no update loop yet" : "") + ").";
                Thread.Sleep(250);
            }
            return null;
        }

        public override Assembly LoadTestAssembly(Lease lease, out LeaseResult refusal)
        {
            refusal = null;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
                .GroupBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var expected in lease.ExpectedModules ?? new List<ModuleIdentity>()) {
                if (!loaded.TryGetValue(expected.Name, out var copies)) {
                    refusal = Refuse(LeaseResult.ModNotLoaded, expected.Name + " is not loaded in the game. Install and enable it in UnityModManager, then restart the game.");
                    return null;
                }
                if (!copies.Any(copy => string.Equals(copy.ManifestModule.ModuleVersionId.ToString("D"), expected.Mvid, StringComparison.OrdinalIgnoreCase))) {
                    refusal = Refuse(LeaseResult.RestartRequired, expected.Name + ": the game loaded build " + string.Join(", ", copies.Select(copy =>
                        copy.ManifestModule.ModuleVersionId.ToString("D") + " from " + Location(copy))) + "; the tests expect build " + expected.Mvid +
                        ". Deploy that build to the Mods folder and restart the game.");
                    return null;
                }
            }
            var source = Path.GetFullPath(lease.TestAssembly ?? throw new ArgumentException("The lease names no test assembly."));
            var hash = Sha256(source);
            if (!string.IsNullOrEmpty(lease.BundleHash) && !string.Equals(lease.BundleHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test assembly changed after the client hashed it; build again and rerun.");
            var name = AssemblyName.GetAssemblyName(source).Name;
            LoadedTests tests;
            lock (this.tests) this.tests.TryGetValue(name, out tests);
            if (tests != null) {
                if (tests.Hash == hash) return Use(lease, tests);
                refusal = Refuse(LeaseResult.RestartRequired, name + " changed since this game process loaded it. Restart the game to load the new build; changed test assemblies are not reloaded in place.");
                return null;
            }
            if (loaded.ContainsKey(name)) throw new InvalidOperationException("The game already loaded an assembly named " + name + "; rename the test project.");
            var directory = Shadow(source, hash, loaded.Keys, lease.SupportFiles ?? new List<string>());
            shadow = directory;
            var assembly = Assembly.LoadFrom(Path.Combine(directory, Path.GetFileName(source)));
            var hostLibrary = typeof(InGameRuntime).Assembly.GetName();
            var reference = assembly.GetReferencedAssemblies().FirstOrDefault(item => item.Name == hostLibrary.Name);
            if (reference != null && reference.Version != hostLibrary.Version) {
                refusal = Refuse(LeaseResult.RestartRequired, "The tests use " + hostLibrary.Name + " " + reference.Version + ", the installed host has " +
                    hostLibrary.Version + ". Install the matching host and restart the game.");
                return null;
            }
            tests = new LoadedTests { Hash = hash, Assembly = assembly, Directory = directory, Source = source };
            lock (this.tests) this.tests[name] = tests;
            return Use(lease, tests);
        }

        private Assembly Use(Lease lease, LoadedTests tests)
        {
            leased = lease;
            current = tests;
            shadow = tests.Directory;
            return tests.Assembly;
        }

        /// <summary>
        /// Copies the runner-owned files, so the build can replace its output while the game keeps them open. Game,
        /// Harmony and mod assemblies are never copied: references bind to the copies the player loaded.
        /// </summary>
        private string Shadow(string source, string hash, IEnumerable<string> loadedNames, IEnumerable<string> supportFiles)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WotR.Testing", "shadow",
                System.Diagnostics.Process.GetCurrentProcess().Id.ToString(), Path.GetFileNameWithoutExtension(source) + "-" + hash.Substring(0, 16));
            if (Directory.Exists(directory)) return directory;
            var excluded = new HashSet<string>(loadedNames, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { managed, Path.Combine(managed, "UnityModManager") })
                foreach (var file in Directory.GetFiles(folder, "*.dll")) excluded.Add(Path.GetFileNameWithoutExtension(file));
            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source)).Concat(supportFiles.Select(Path.GetFullPath))) {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension != ".dll" && extension != ".pdb" && extension != ".json" && extension != ".config") continue;
                var assemblyName = extension == ".dll" || extension == ".pdb" ? Path.GetFileNameWithoutExtension(file) : null;
                if (assemblyName != null && excluded.Contains(assemblyName) && !string.Equals(file, source, StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);
            }
            // The bridge binds to this test project's xUnit only from the same directory. The mod folder keeps it under
            // another extension, so mods that load every DLL in the Mods folders cannot load the wrong copy first.
            File.Copy(Path.Combine(Path.GetDirectoryName(typeof(TestBridges).Assembly.Location), TestBridges.XunitAssembly + ".bridge"),
                Path.Combine(staging, TestBridges.XunitAssembly + ".dll"), true);
            try { Directory.Move(staging, directory); }
            catch (IOException) when (Directory.Exists(directory)) { Directory.Delete(staging, true); }
            return directory;
        }

        public override ITestBridge CreateBridge(Assembly tests)
        {
            var bridge = TestBridges.LoadXunit(current.Directory, tests);
            // LoadFrom returns an already loaded copy with the same identity; it is usable only beside an xUnit.
            var location = Path.GetDirectoryName(bridge.GetType().Assembly.Location);
            if (!File.Exists(Path.Combine(location, "xunit.execution.desktop.dll")))
                throw new InvalidOperationException("The xUnit bridge was loaded from " + location + ", where no xUnit is available. Restart the game.");
            return bridge;
        }

        public override void SessionStarting(CancellationToken canceled)
        {
            session = CancellationTokenSource.CreateLinkedTokenSource(canceled);
            InGameRuntime.SessionCancellation = session.Token;
            // An unfocused game window would otherwise stop updating and stall every frame wait.
            dispatcher.Run(() => {
                runInBackground = Application.runInBackground;
                Application.runInBackground = true;
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        public override void SessionEnded()
        {
            session?.Cancel();
            InGameRuntime.SessionCancellation = CancellationToken.None;
            var previous = runInBackground;
            if (previous.HasValue) dispatcher.Run(() => {
                Application.runInBackground = previous.Value;
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        public override string EnvironmentReport(Lease lease)
        {
            var harmony = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "0Harmony");
            return ProtocolJson.Serialize(new HostReport {
                Mode = Mode, RuntimeKind = RuntimeKind, HostVersion = HostVersion, UnityVersion = unityVersion, GameVersion = gameVersion,
                InstallRoot = installRoot, Readiness = readiness, MainThreadId = MainThreadId, FrameCount = FrameCount, Dirty = Dirty,
                TestAssembly = current?.Source, TestAssemblyHash = current?.Hash, ShadowDirectory = current?.Directory,
                Harmony = harmony == null ? null : harmony.GetName().Version + " " + Location(harmony),
                Mods = UnityModManager.modEntries.Select(mod => new ModReport {
                    Id = mod.Info.Id, Version = mod.Info.Version, Active = mod.Active,
                    Assembly = mod.Assembly == null ? null : Location(mod.Assembly),
                    Mvid = mod.Assembly?.ManifestModule.ModuleVersionId.ToString("D")
                }).ToList()
            });
        }

        private static LeaseResult Refuse(string reason, string detail) => new LeaseResult { Reason = reason, Detail = detail };

        private static string Location(Assembly assembly)
        {
            try { return string.IsNullOrEmpty(assembly.Location) ? "(in memory)" : assembly.Location; }
            catch (NotSupportedException) { return "(dynamic)"; }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class LoadedTests
        {
            internal string Hash;
            internal string Source;
            internal string Directory;
            internal Assembly Assembly;
        }

        [DataContract]
        private sealed class HostReport
        {
            [DataMember(Name = "mode", Order = 0)] public string Mode { get; set; }
            [DataMember(Name = "runtimeKind", Order = 1)] public string RuntimeKind { get; set; }
            [DataMember(Name = "hostVersion", Order = 2)] public string HostVersion { get; set; }
            [DataMember(Name = "unityVersion", Order = 3)] public string UnityVersion { get; set; }
            [DataMember(Name = "gameVersion", Order = 4)] public string GameVersion { get; set; }
            [DataMember(Name = "installRoot", Order = 5)] public string InstallRoot { get; set; }
            [DataMember(Name = "readiness", Order = 6)] public string Readiness { get; set; }
            [DataMember(Name = "mainThreadId", Order = 7)] public int MainThreadId { get; set; }
            [DataMember(Name = "frameCount", Order = 8)] public long FrameCount { get; set; }
            [DataMember(Name = "dirty", Order = 9)] public string Dirty { get; set; }
            [DataMember(Name = "testAssembly", Order = 10)] public string TestAssembly { get; set; }
            [DataMember(Name = "testAssemblyHash", Order = 11)] public string TestAssemblyHash { get; set; }
            [DataMember(Name = "shadowDirectory", Order = 12)] public string ShadowDirectory { get; set; }
            [DataMember(Name = "harmony", Order = 13)] public string Harmony { get; set; }
            [DataMember(Name = "mods", Order = 14)] public List<ModReport> Mods { get; set; }
        }

        [DataContract]
        private sealed class ModReport
        {
            [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
            [DataMember(Name = "version", Order = 1)] public string Version { get; set; }
            [DataMember(Name = "active", Order = 2)] public bool Active { get; set; }
            [DataMember(Name = "assembly", Order = 3)] public string Assembly { get; set; }
            [DataMember(Name = "mvid", Order = 4)] public string Mvid { get; set; }
        }
    }
}
