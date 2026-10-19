using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Runner
{
    /// <summary>What differs between the in-game host and the offline worker.</summary>
    public abstract class RunnerEnvironment
    {
        /// <summary>The AppDomain data key naming the process's test mode, set before any test code loads.</summary>
        public const string ModeKey = "WotR.Testing.Mode";

        private string dirty;

        /// <summary><see cref="Welcome.InGame"/> or <see cref="Welcome.OffGame"/>.</summary>
        public abstract string Mode { get; }
        public abstract string RuntimeKind { get; }
        public virtual string GameVersion => null;
        public virtual string UnityVersion => null;
        public virtual string InstallRoot => null;
        public virtual int MainThreadId => 0;
        /// <summary>The player's frame count, or -1 outside the game.</summary>
        public virtual long FrameCount => -1;
        public virtual string Readiness => "ready";
        public static string HostVersion => PackageVersion(typeof(RunnerEnvironment).Assembly);

        /// <summary>Why the process can no longer run tests reliably, or null.</summary>
        public string Dirty => dirty;

        public void MarkDirty(string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "unspecified";
            System.Threading.Interlocked.CompareExchange(ref dirty, reason, null);
        }

        /// <summary>Assemblies the process loaded, for comparison with the builds the client expects.</summary>
        public virtual IReadOnlyList<ModuleIdentity> LoadedModules()
            => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic)
                .Select(assembly => new ModuleIdentity { Name = assembly.GetName().Name, Mvid = assembly.ManifestModule.ModuleVersionId.ToString("D") })
                .OrderBy(module => module.Name, StringComparer.Ordinal).ToList();

        /// <summary>Waits until tests can run, or returns why not; the in-game host waits for the game to load.</summary>
        public virtual string WaitUntilReady(TimeSpan timeout, CancellationToken cancellationToken) => null;

        /// <summary>Loads the leased test assembly, or returns why the lease is refused.</summary>
        public abstract Assembly LoadTestAssembly(Lease lease, out LeaseResult refusal);

        public virtual void SessionStarting(System.Threading.CancellationToken canceled) { }
        public virtual void SessionEnded() { }

        /// <summary>Runs code that touches the game where it must run: Unity's main thread in the player.</summary>
        public virtual Task<T> RunInContext<T>(Func<Task<T>> body) => body();

        /// <summary>Partition details for the client's environment report, as JSON.</summary>
        public virtual string EnvironmentReport(Lease lease) => null;

        /// <summary>The test framework bridge for a leased assembly; by default the one beside this library.</summary>
        public virtual ITestBridge CreateBridge(Assembly tests)
            => TestBridges.LoadXunit(System.IO.Path.GetDirectoryName(typeof(RunnerEnvironment).Assembly.Location), tests);

        public static string PackageVersion(Assembly assembly)
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version.ToString();
            var metadata = version.IndexOf('+');
            return metadata < 0 ? version : version.Substring(0, metadata);
        }
    }
}
