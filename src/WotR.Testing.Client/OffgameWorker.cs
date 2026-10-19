using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>
    /// A worker process for one offgame partition: wotr-mono-host on the game's Unity Mono (or .NET Framework for netfx)
    /// running the test assembly with the offline library, reached over the same protocol as the in-game host.
    /// </summary>
    public sealed class OffgameWorker : IDisposable
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);
        private readonly Process process;
        private readonly StringBuilder errors = new StringBuilder();

        private OffgameWorker(Process process) => this.process = process;

        public int ProcessId => process.Id;
        public HostClient Client { get; private set; }

        public static async Task<OffgameWorker> StartAsync(string runtime, string inputRoot, string monoHost, string workDirectory, string testAssembly,
            string reportDirectory, string runId, Action<string> log, CancellationToken cancellationToken)
        {
            if (runtime != "mono" && runtime != "netfx") throw new ArgumentException("WotrRuntime must be mono or netfx.");
            if (!File.Exists(monoHost)) throw new FileNotFoundException("wotr-mono-host was not found.", monoHost);
            var workers = Path.Combine(Path.GetFullPath(workDirectory), "workers");
            Directory.CreateDirectory(workers);
            var endpoint = Path.Combine(workers, Guid.NewGuid().ToString("N") + ".port");
            var token = HostAdvertisement.Secret(32);
            var worker = new List<string> { "--worker", "--endpoint-file", endpoint, "--token", token, "--source", Path.GetFullPath(testAssembly),
                "--parent", Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var arguments = new List<string>();
            if (runtime == "mono") {
                var embed = Path.Combine(inputRoot, "MonoBleedingEdge", "EmbedRuntime");
                if (!File.Exists(Path.Combine(embed, "mono-2.0-bdwgc.dll")))
                    throw new FileNotFoundException("[ENV_MISSING] The game's Unity Mono runtime was not found in " + embed);
                var classLibrary = WotR.Testing.VSTest.MonoClassLibrary.Prepare(Path.Combine(inputRoot, "Wrath_Data", "Managed"), workDirectory);
                arguments.AddRange(new[] { "--runtime", embed, "--config", Path.Combine(inputRoot, "MonoBleedingEdge", "etc"), "--assemblies", classLibrary, "--", monoHost });
            }
            arguments.AddRange(worker);
            var start = new ProcessStartInfo(monoHost, string.Join(" ", arguments.Select(Quote))) {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(testAssembly))
            };
            if (!string.IsNullOrEmpty(reportDirectory)) start.EnvironmentVariables["WOTR_OFFLINE_REPORT_DIR"] = reportDirectory;
            var result = new OffgameWorker(new Process { StartInfo = start, EnableRaisingEvents = true });
            result.process.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
            result.process.ErrorDataReceived += (_, e) => {
                if (e.Data == null) return;
                lock (result.errors) if (result.errors.Length < 65536) result.errors.AppendLine(e.Data);
                log?.Invoke(e.Data);
            };
            result.process.Start();
            result.process.BeginOutputReadLine();
            result.process.BeginErrorReadLine();
            try {
                var deadline = DateTime.UtcNow + StartTimeout;
                while (!File.Exists(endpoint)) {
                    if (result.process.HasExited)
                        throw new InvalidOperationException("[INIT_FAILED] The offgame worker exited with code " + result.process.ExitCode + " before it listened.\n" + result.Errors);
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("The offgame worker did not start within " + StartTimeout.TotalSeconds + " seconds.");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                var port = int.Parse(File.ReadAllText(endpoint), System.Globalization.CultureInfo.InvariantCulture);
                File.Delete(endpoint);
                result.Client = await HostClient.ConnectAsync(port, token, runId, GameConnection.RunnerVersion, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch {
                result.Dispose();
                throw;
            }
        }

        public string Errors { get { lock (errors) return errors.ToString(); } }

        /// <summary>The worker exits after its session; a worker that does not is terminated.</summary>
        public void Dispose()
        {
            Client?.Dispose();
            try {
                if (!process.WaitForExit(30000)) process.Kill();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

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
