using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Client
{
    /// <summary>How the in-game partition reaches a player: auto attaches to a running host, or launches when no game runs.</summary>
    public static class ConnectionModes
    {
        public const string Auto = "auto";
        public const string Attach = "attach";
        public const string Launch = "launch";

        public static bool IsValid(string mode) => mode == Auto || mode == Attach || mode == Launch;
    }

    /// <summary>A connection failure the user can act on; <see cref="Code"/> names the condition.</summary>
    public sealed class ConnectionException : Exception
    {
        public const string HostNotLoaded = "host-not-loaded";
        public const string NoGame = "no-game";
        public const string Ambiguous = "ambiguous-host";
        public const string LaunchFailed = "launch-failed";
        public const string ProtocolMismatch = "protocol-mismatch";
        public const string HostMismatch = "host-version-mismatch";
        public string Code { get; }
        public ConnectionException(string code, string message, Exception inner = null) : base("[" + code + "] " + message, inner) => Code = code;
    }

    /// <summary>A player with the WotR.Testing host, attached or launched by this run.</summary>
    public sealed class GameConnection
    {
        public const string GameExecutable = "Wrath.exe";
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(15);
        /// <summary>Termination finishes asynchronously; the next run must not find the process still exiting.</summary>
        public static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(30);
        /// <summary>How long a game process without a window (starting or exiting) may take before it counts as running.</summary>
        public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);

        private GameConnection(HostAdvertisement host, string mode, bool owned)
        {
            Host = host;
            Mode = mode;
            Owned = owned;
        }

        public HostAdvertisement Host { get; }
        /// <summary>attach or launch: how this run reached the player.</summary>
        public string Mode { get; }
        /// <summary>Started by this run; only an owned process is ever shut down.</summary>
        public bool Owned { get; }

        /// <summary>
        /// Finds a compatible host for <paramref name="installRoot"/>, launching the game only when the policy allows it
        /// and no game from that installation runs. Never falls back from a running game without the host.
        /// </summary>
        public static async Task<GameConnection> ConnectAsync(string mode, string installRoot, int processId, TimeSpan startupTimeout,
            bool launchInBackground, Action<string> log, CancellationToken cancellationToken)
        {
            if (!ConnectionModes.IsValid(mode)) throw new ArgumentException("WotrInGameConnection must be auto, attach or launch.");
            installRoot = Normalize(installRoot);
            var hosts = Matching(installRoot, processId);
            List<Process> running;
            // A game without a window is starting (its host may still advertise) or exiting (for example the one the previous
            // run closed). Neither is a running game without the host yet.
            var settle = DateTime.UtcNow + SettleTime;
            while (hosts.Count == 0 && (running = GameProcesses(installRoot, processId)).Count > 0 &&
                running.Any(process => !HasWindow(process)) && DateTime.UtcNow < settle) {
                log?.Invoke("Waiting for game process " + string.Join(", ", running.Select(p => p.Id)) + " to finish starting or exiting.");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                hosts = Matching(installRoot, processId);
            }
            if (hosts.Count == 1) {
                if (mode == ConnectionModes.Launch && processId == 0) {
                    throw new ConnectionException(ConnectionException.Ambiguous, "launch was requested, but the game already runs with the host (process " +
                        hosts[0].ProcessId + "). Use attach or auto, or close the game.");
                }
                return new GameConnection(hosts[0], ConnectionModes.Attach, false);
            }
            if (hosts.Count > 1)
                throw new ConnectionException(ConnectionException.Ambiguous, "Several running games have the host (processes " +
                    string.Join(", ", hosts.Select(host => host.ProcessId)) + "). Choose one with WotrInGamePid.");
            running = GameProcesses(installRoot, processId);
            if (running.Count > 0)
                throw new ConnectionException(ConnectionException.HostNotLoaded, "The game runs (process " + string.Join(", ", running.Select(p => p.Id)) +
                    ") without the WotR.Testing host. Install it with the InstallWotrInGameHost target, enable it in UnityModManager and restart the game.");
            if (processId != 0) throw new ConnectionException(ConnectionException.NoGame, "Process " + processId + " is not a running game.");
            if (mode == ConnectionModes.Attach)
                throw new ConnectionException(ConnectionException.NoGame, "No running game with the WotR.Testing host; start the game or use WotrInGameConnection=auto.");
            return await LaunchAsync(installRoot, startupTimeout, launchInBackground, log, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<GameConnection> LaunchAsync(string installRoot, TimeSpan startupTimeout, bool background, Action<string> log, CancellationToken cancellationToken)
        {
            var executable = Path.Combine(installRoot, GameExecutable);
            if (!File.Exists(executable)) throw new ConnectionException(ConnectionException.LaunchFailed, "The game executable was not found: " + executable);
            var before = new HashSet<int>(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExecutable)).Select(process => process.Id));
            var started = DateTime.Now;
            log?.Invoke("Starting " + executable + "; waiting up to " + startupTimeout.TotalSeconds + " seconds for the WotR.Testing host.");
            // Read by the host of every process this launch produces, including one that Steam starts in its place.
            LaunchRequest.Write(background, background ? GetForegroundWindow().ToInt64() : 0);
            try {
                using (Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = installRoot, UseShellExecute = true })) { }
            }
            catch (Exception error) { throw new ConnectionException(ConnectionException.LaunchFailed, "The game could not be started: " + error.Message, error); }
            // Steam may replace the started process with one it starts itself, after mods have already loaded in the first
            // one. Any game process that was not running before this launch belongs to this run; the one that finishes
            // loading is the one to use.
            var deadline = DateTime.UtcNow + startupTimeout;
            string state = "no host yet";
            while (DateTime.UtcNow < deadline) {
                cancellationToken.ThrowIfCancellationRequested();
                var owned = Matching(installRoot, 0).Where(host => !before.Contains(host.ProcessId) &&
                    HostAdvertisement.StartTimeOf(host.ProcessId) is DateTime time && time >= started.AddSeconds(-1)).ToList();
                foreach (var host in owned) {
                    try {
                        using (var probe = await HostClient.ConnectAsync(host.Port, host.Token, "launch-probe", RunnerVersion, ConnectTimeout, cancellationToken).ConfigureAwait(false)) {
                            state = "process " + host.ProcessId + ": " + probe.Host.Readiness;
                            if (IsLoaded(probe.Host.Readiness)) {
                                LaunchRequest.Remove();
                                return new GameConnection(host, ConnectionModes.Launch, true);
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException || error is ProtocolException || error is TimeoutException || error is System.Net.Sockets.SocketException) {
                        state = "process " + host.ProcessId + ": " + error.Message;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            throw new ConnectionException(ConnectionException.LaunchFailed, "The started game did not finish loading with the WotR.Testing host within " +
                startupTimeout.TotalSeconds + " seconds (" + state + "). Is the host installed and enabled in UnityModManager?");
        }

        /// <summary>The host's update loop runs and no loading screen is active.</summary>
        public static bool IsLoaded(string readiness) => readiness == "main-menu" || (readiness?.StartsWith("area:", StringComparison.Ordinal) ?? false);

        public Task<HostClient> OpenSessionAsync(string runId, CancellationToken cancellationToken)
            => HostClient.ConnectAsync(Host.Port, Host.Token, runId, RunnerVersion, ConnectTimeout, cancellationToken);

        /// <summary>Closes a game this run started: first its window, then, after the grace period, the process.</summary>
        public async Task ShutdownOwnedAsync(Action<string> log)
        {
            if (!Owned) return;
            Process process;
            try { process = Process.GetProcessById(Host.ProcessId); }
            catch (ArgumentException) { return; }
            using (process) {
                // The process must still be the one this run started.
                if (process.HasExited || !HostAdvertisement.SameTime(process.StartTime, Host.ProcessStartTime)) return;
                log?.Invoke("Closing the game started by this run (process " + process.Id + ").");
                process.CloseMainWindow();
                var deadline = DateTime.UtcNow + ShutdownGrace;
                while (!process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(250).ConfigureAwait(false);
                if (!process.HasExited) {
                    log?.Invoke("The game did not exit within " + ShutdownGrace.TotalSeconds + " seconds; terminating process " + process.Id + ".");
                    process.Kill();
                    deadline = DateTime.UtcNow + TerminationWait;
                    while (!process.HasExited && DateTime.UtcNow < deadline) await Task.Delay(250).ConfigureAwait(false);
                    if (!process.HasExited) log?.Invoke("Process " + process.Id + " was still exiting " + TerminationWait.TotalSeconds + " seconds after termination.");
                }
                if (process.HasExited) log?.Invoke("The game started by this run exited.");
            }
        }

        public static string RunnerVersion => typeof(GameConnection).Assembly.GetName().Version.ToString();

        private static List<HostAdvertisement> Matching(string installRoot, int processId)
            => HostAdvertisement.ReadLive().Where(host => host.Mode == Welcome.InGame && host.InstallRoot != null &&
                    string.Equals(Normalize(host.InstallRoot), installRoot, StringComparison.OrdinalIgnoreCase) &&
                    (processId == 0 || host.ProcessId == processId)).ToList();

        private static List<Process> GameProcesses(string installRoot, int processId)
        {
            var result = new List<Process>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExecutable))) {
                if (processId != 0 && process.Id != processId) continue;
                try {
                    if (process.HasExited) continue;
                }
                catch (Exception) { /* No access: assume it runs. */ }
                string path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception) { /* Another user's process or no access: count it only by name. */ }
                if (path == null || string.Equals(Normalize(Path.GetDirectoryName(path)), installRoot, StringComparison.OrdinalIgnoreCase)) result.Add(process);
            }
            return result;
        }

        private static bool HasWindow(Process process)
        {
            try {
                process.Refresh();
                return process.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception) { return true; }
        }

        internal static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    }
}
