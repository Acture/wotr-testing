using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace WotR.Testing.MonoHost
{
    /// <summary>Installs offline assembly resolution before the SDK testhost reflects over test types.</summary>
    internal static class ManagedTestHost
    {
        internal static int Run(string[] args)
        {
            try
            {
                if (args.Length < 2) throw new ArgumentException("--testhost requires the SDK host and test assembly paths.");
                var host = Path.GetFullPath(args[0]);
                var source = Path.GetFullPath(args[1]);
                if (!File.Exists(host)) throw new FileNotFoundException("The SDK testhost was not found.", host);
                if (!File.Exists(source)) throw new FileNotFoundException("The test assembly was not found.", source);
                var setup = new AppDomainSetup
                {
                    ApplicationBase = Path.GetDirectoryName(source),
                    ConfigurationFile = File.Exists(source + ".config") ? source + ".config" : host + ".config",
                    ShadowCopyFiles = "false"
                };
                var domain = AppDomain.CreateDomain("WotRTestHostAppDomain", null, setup);
                domain.SetData("WotR.Testing.Mode", "offgame");
                var worker = (ManagedTestHostWorker)domain.CreateInstanceFromAndUnwrap(
                    typeof(ManagedTestHostWorker).Assembly.Location, typeof(ManagedTestHostWorker).FullName);
                // Keep the domain until process exit, as the ordinary SDK testhost does.
                return worker.Run(host, args.Skip(2).ToArray());
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("wotr-mono-host: testhost startup failed: " + error);
                return 2;
            }
        }
    }

    /// <summary>Runs in the test assembly's domain; contains no game or test-framework references.</summary>
    public sealed class ManagedTestHostWorker : MarshalByRefObject
    {
        public override object InitializeLifetimeService() => null;

        public int Run(string host, string[] args)
        {
            var hostDirectory = Path.GetDirectoryName(host);
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
            {
                var name = new AssemblyName(request.Name);
                foreach (var directory in new[] { hostDirectory, Path.Combine(hostDirectory, "Extensions") })
                {
                    var path = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };

            // Test SDK packages can copy older TestPlatform DLLs beside the tests with the same assembly
            // identity as the running SDK. Mono keeps the first copy, so retain the SDK host's own graph
            // before the consumer directory can supply those names. Framework and game libraries stay lazy.
            foreach (var path in Directory.EnumerateFiles(hostDirectory, "*.dll")
                .Where(path => Path.GetFileName(path).StartsWith("Microsoft.TestPlatform.", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).StartsWith("Microsoft.VisualStudio.TestPlatform.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                Assembly.LoadFrom(path);

            // Reflection alone need not execute module initializers. Install the resolver explicitly before
            // the framework adapter inspects game-typed fields, base classes or method signatures.
            var offlinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WotR.Testing.Offline.dll");
            var offline = Assembly.LoadFrom(offlinePath);
            Invoke(offline.GetType("WotR.Testing.Offline.OfflineRuntime", true)
                .GetMethod("InstallResolver", BindingFlags.Public | BindingFlags.Static), null);

            var entry = Assembly.LoadFrom(host).EntryPoint
                ?? throw new MissingMethodException("The SDK testhost has no entry point.");
            var result = Invoke(entry, entry.GetParameters().Length == 0 ? null : new object[] { args });
            if (result is Task<int> exitTask) return exitTask.GetAwaiter().GetResult();
            if (result is Task task) task.GetAwaiter().GetResult();
            return result is int exitCode ? exitCode : Environment.ExitCode;
        }

        private static object Invoke(MethodInfo method, object[] arguments)
        {
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
