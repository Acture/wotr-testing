using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Host;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace WotR.Testing.VSTest
{
    /// <summary>Starts the ordinary VSTest testhost on the game's Unity Mono runtime.</summary>
    [ExtensionUri("HostProvider://WotR/UnityMono")]
    [FriendlyName("WotR Unity Mono")]
    public sealed class MonoRuntimeProvider : ITestRuntimeProvider
    {
        private XElement settings;
        private Process process;
        private ITestHostLauncher launcher;
        private readonly StringBuilder errors = new StringBuilder();
        private readonly object processGate = new object();
        public bool Shared => false;
        public event EventHandler<HostProviderEventArgs> HostLaunched;
        public event EventHandler<HostProviderEventArgs> HostExited;

        public bool CanExecuteCurrentRunConfiguration(string runsettingsXml)
            => !string.IsNullOrEmpty(runsettingsXml)
               && (string)XDocument.Parse(runsettingsXml).Root.Element("WotR")?.Element("Runtime") == "mono";

        public void Initialize(IMessageLogger logger, string runsettingsXml)
            => settings = XDocument.Parse(runsettingsXml).Root.Element("WotR")
                ?? throw new InvalidOperationException("Missing WotR test settings.");

        public void SetCustomLauncher(ITestHostLauncher customLauncher) => launcher = customLauncher;
        public TestHostConnectionInfo GetTestHostConnectionInfo()
            => new TestHostConnectionInfo { Endpoint = "127.0.0.1:0", Role = ConnectionRole.Client, Transport = Transport.Sockets };
        public IEnumerable<string> GetTestSources(IEnumerable<string> sources) => sources;
        public IEnumerable<string> GetTestPlatformExtensions(IEnumerable<string> sources, IEnumerable<string> extensions)
            => extensions.Concat(sources.SelectMany(s => Directory.EnumerateFiles(Path.GetDirectoryName(s), "*TestAdapter.dll")))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        public TestProcessStartInfo GetTestHostProcessStartInfo(IEnumerable<string> sources,
            IDictionary<string, string> environmentVariables, TestRunnerConnectionInfo connectionInfo)
        {
            var source = sources.Single();
            var configured = environmentVariables?.FirstOrDefault(p => string.Equals(p.Key, "WOTR_INPUT_ROOT", StringComparison.OrdinalIgnoreCase));
            var inputRoot = configured?.Key != null ? configured.Value.Value : Environment.GetEnvironmentVariable("WOTR_INPUT_ROOT");
            if (string.IsNullOrEmpty(inputRoot)) inputRoot = Value("InputRoot");
            var mono = Path.Combine(inputRoot, "MonoBleedingEdge", "EmbedRuntime");
            if (!File.Exists(Path.Combine(mono, "mono-2.0-bdwgc.dll")))
                throw new FileNotFoundException("[ENV_MISSING] The game's Unity Mono runtime was not found in " + mono);
            var classLibrary = MonoClassLibrary.Prepare(Path.Combine(inputRoot, "Wrath_Data", "Managed"), Value("WorkDirectory"));
            var platform = Path.GetDirectoryName(typeof(ITestRuntimeProvider).Assembly.Location);
            var host = new[] {
                Path.Combine(platform, "TestHostNetFramework", "testhost.net48.exe"),
                Path.Combine(platform, "testhost.net48.exe"),
                Path.Combine(platform, "TestHostNetFramework", "testhost.exe"),
                Path.Combine(platform, "testhost.exe")
            }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("The VSTest .NET Framework testhost was not found beside " + platform);
            var isolated = TestHostIsolation.Prepare(host, Value("WorkDirectory"));
            host = isolated.Host;
            var args = new[] { "--runtime", mono, "--config", Path.Combine(inputRoot, "MonoBleedingEdge", "etc"),
                "--assemblies", classLibrary, "--", host };
            return new TestProcessStartInfo {
                FileName = Value("MonoHost"),
                Arguments = string.Join(" ", args.Select(Quote)) + " " + connectionInfo.ToCommandLineOptions()
                    + " --testsourcepath " + Quote(source),
                WorkingDirectory = Path.GetDirectoryName(source),
                EnvironmentVariables = new Dictionary<string, string>(environmentVariables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) {
                    ["WOTR_VSTEST_HOST_INFO"] = isolated.Manifest
                }
            };
        }

        public Task<bool> LaunchTestHostAsync(TestProcessStartInfo info, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (launcher != null)
                throw new NotSupportedException("Debugging the embedded Unity Mono runtime through a CLR debugger is not supported.");
            var start = new ProcessStartInfo(info.FileName, info.Arguments) {
                WorkingDirectory = info.WorkingDirectory, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
            };
            foreach (var pair in info.EnvironmentVariables) start.Environment[pair.Key] = pair.Value;
            var child = new Process { StartInfo = start, EnableRaisingEvents = true };
            child.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { if (errors.Length < 65536) errors.AppendLine(e.Data); } };
            child.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
            child.Exited += (_, __) => {
                HostProviderEventArgs exited;
                lock (processGate) {
                    if (process != child) return; // CleanTestHostAsync already owns this process.
                    lock (errors) exited = new HostProviderEventArgs(errors.ToString(), child.ExitCode, child.Id);
                }
                HostExited?.Invoke(this, exited);
            };
            lock (processGate) process = child;
            if (!child.Start()) return Task.FromResult(false);
            child.BeginErrorReadLine();
            child.BeginOutputReadLine();
            HostLaunched?.Invoke(this, new HostProviderEventArgs("", 0, child.Id));
            return Task.FromResult(true);
        }

        public Task CleanTestHostAsync(CancellationToken cancellationToken)
        {
            Process child;
            lock (processGate) { child = process; process = null; }
            if (child != null) {
                try { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                finally { child.Dispose(); }
            }
            return Task.CompletedTask;
        }

        private string Value(string name) => (string)settings.Element(name)
            ?? throw new InvalidOperationException("Missing WotR setting: " + name);

        // Windows command-line quoting, including a trailing backslash before the closing quote.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value) {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
