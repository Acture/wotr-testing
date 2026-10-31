using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Runner
{
    /// <summary>A test framework adapter inside the runner process. Runner code never references a test framework.</summary>
    public interface ITestBridge
    {
        IReadOnlyList<string> Diagnostics { get; }
        IReadOnlyList<TestDescriptor> Discover();

        /// <summary>Runs the selected cases one at a time, streaming TestStarted and TestFinished events.</summary>
        Task<RunCounts> Run(IReadOnlyCollection<string> ids, Run request, RunnerEnvironment environment, EventWriter writer,
            TimeSpan testTimeout, CancellationToken cancellationToken);
    }

    public sealed class RunCounts
    {
        public int Passed;
        public int Failed;
        public int Skipped;
        public string Error;
    }

    public static class TestBridges
    {
        public const string XunitAssembly = "WotR.Testing.Runner.Xunit";
        public const string FuzzAssembly = "WotR.Testing.Runner.Fuzzing";

        /// <summary>The bridge assembly for a workload: ordinary xUnit tests or registered fuzz targets.</summary>
        public static string AssemblyFor(string kind) => kind == TestDescriptor.FuzzKind ? FuzzAssembly : XunitAssembly;

        /// <summary>
        /// Loads a bridge from <paramref name="directory"/>. Next to the test project's own xUnit or fuzzing library, its
        /// references bind the same way as the test assembly's, whatever the host process's probing rules are.
        /// </summary>
        public static ITestBridge Load(string directory, string kind, Assembly tests)
        {
            var name = AssemblyFor(kind);
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path)) throw new FileNotFoundException("The runner's " + kind + " bridge is missing.", path);
            var type = Assembly.LoadFrom(path).GetType(name + (kind == TestDescriptor.FuzzKind ? ".FuzzBridge" : ".XunitBridge"), true);
            return (ITestBridge)Activator.CreateInstance(type, tests);
        }
    }
}
