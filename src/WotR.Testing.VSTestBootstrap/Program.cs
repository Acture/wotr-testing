using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml;
using System.Xml.Linq;

namespace WotR.Testing.VSTestBootstrap
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try {
                // Beyond MAX_PATH the host reports a \\?\ path, which XML loading by name misreads as a network host.
                var baseDirectory = AppContext.BaseDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) ? AppContext.BaseDirectory.Substring(4) : AppContext.BaseDirectory;
                XElement settings;
                using (var stream = File.OpenRead(Path.Combine(baseDirectory, "bootstrap.xml"))) settings = XDocument.Load(stream).Root;
                if (Environment.GetEnvironmentVariable("WOTR_VSTEST_ARGUMENTS_LOG") is string trace && trace.Length > 0) File.WriteAllLines(trace, args);
                var consolePath = settings.Element("Console").Value;
                var resolver = new AssemblyDependencyResolver(consolePath);
                AssemblyLoadContext.Default.Resolving += (_, name) => {
                    // Offline already depends on Cecil; use the consumer's restored copy for local SDK host isolation.
                    if (name.Name == "Mono.Cecil") {
                        var cecil = Path.Combine(settings.Element("SourceDirectory").Value, "Mono.Cecil.dll");
                        if (File.Exists(cecil)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(cecil);
                    }
                    var resolved = resolver.ResolveAssemblyToPath(name);
                    if (resolved == null) {
                        var beside = Path.Combine(Path.GetDirectoryName(consolePath), name.Name + ".dll");
                        if (File.Exists(beside)) resolved = beside;
                    }
                    return resolved == null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(resolved);
                };
                // Apply the SDK's own path/environment expansion to the original settings location before our merged
                // file is read by the CLI. This preserves MSTest settings files and other supported relative paths.
                var utilities = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(consolePath), "Microsoft.TestPlatform.Utilities.dll"));
                var document = new XmlDocument();
                document.Load(settings.Element("PreparedSettings").Value);
                utilities.GetType("Microsoft.VisualStudio.TestPlatform.Utilities.ClientUtilities", true)
                    .GetMethod("FixRelativePathsInRunSettings").Invoke(null, new object[] { document, settings.Element("SourceSettings").Value });
                document.Save(settings.Element("PreparedSettings").Value);
                // VSTest constructs its runtime-provider manager before parsing /TestAdapterPath. Register our provider
                // through the public cache API before entering the SDK's unmodified CLI. All CLI arguments are forwarded.
                var common = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(consolePath), "Microsoft.VisualStudio.TestPlatform.Common.dll"));
                var cacheType = common.GetType("Microsoft.VisualStudio.TestPlatform.Common.ExtensionFramework.TestPluginCache", true);
                var cache = cacheType.GetProperty("Instance").GetValue(null);
                cacheType.GetMethod("UpdateExtensions").Invoke(cache, new object[] { new[] { settings.Element("Provider").Value }, false });
                args = Sources(settings.Element("Sources"), args);
                var console = AssemblyLoadContext.Default.LoadFromAssemblyPath(consolePath);
                return (int)console.EntryPoint.Invoke(null, new object[] { args });
            }
            catch (Exception error) {
                Console.Error.WriteLine("WotR VSTest startup failed: " + (error is TargetInvocationException ? error.InnerException : error));
                return 1;
            }
        }

        /// <summary>
        /// Applies the planned sources: the test assembly stays only for ordinary offgame tests, a .wotrtests manifest
        /// carries the other partitions, and a mode clause keeps in-game cases away from the ordinary xUnit adapter.
        /// </summary>
        private static string[] Sources(XElement sources, string[] args)
        {
            if (sources == null || sources.Element("TestAssembly") == null) return args;
            var assembly = Path.GetFullPath(sources.Element("TestAssembly").Value);
            var result = args.Where(arg => (bool)sources.Element("Keep") || !SamePath(arg, assembly)).ToList();
            result.AddRange(sources.Elements("Add").Select(source => source.Value));
            var clause = (string)sources.Element("Filter");
            if (clause != null) {
                var filter = result.FindIndex(arg => arg.StartsWith("--testCaseFilter:", StringComparison.OrdinalIgnoreCase));
                if (filter < 0) result.Add("--testCaseFilter:" + clause);
                else result[filter] = "--testCaseFilter:(" + result[filter].Substring("--testCaseFilter:".Length) + ")&(" + clause + ")";
            }
            return result.ToArray();
        }

        private static bool SamePath(string argument, string path)
        {
            try { return !argument.StartsWith("-", StringComparison.Ordinal) && string.Equals(Path.GetFullPath(argument), path, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
    }
}
