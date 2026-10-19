using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Runner
{
    /// <summary>
    /// Accepts adapter connections on a loopback TCP port; a connection must present the advertised token. Each
    /// connection is one session; only one session at a time holds the lease, so tests never run concurrently.
    /// </summary>
    /// <remarks>
    /// Not a named pipe: Unity Mono's pipe server cannot wait on an overlapped handle, and synchronous pipe handles
    /// serialize a pending read with writes, which would stall heartbeats while the host waits for a request.
    /// </remarks>
    public sealed class RunnerServer : IDisposable
    {
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
        private readonly RunnerEnvironment environment;
        private readonly HostAdvertisement advertisement;
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Thread acceptor;
        private readonly object leaseGate = new object();
        private Session leaseHolder;
        private volatile bool stopped;

        public RunnerServer(RunnerEnvironment environment, HostAdvertisement advertisement)
        {
            this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
            this.advertisement = advertisement ?? throw new ArgumentNullException(nameof(advertisement));
            acceptor = new Thread(Accept) { IsBackground = true, Name = "WotR.Testing server" };
        }

        public event Action<string> Log;

        /// <summary>Raised after a session's connection is closed, so its final events have been delivered.</summary>
        public event Action SessionClosed;

        /// <summary>Starts listening and records the port in the advertisement.</summary>
        public void Start()
        {
            listener.Start();
            advertisement.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptor.Start();
        }

        private void Accept()
        {
            while (!stopped) {
                try {
                    var connection = listener.AcceptTcpClient();
                    if (stopped) { connection.Close(); return; }
                    connection.NoDelay = true;
                    Task.Run(() => new Session(this, connection.GetStream()).Serve());
                }
                catch (Exception error) {
                    if (stopped) return;
                    Log?.Invoke("Server error: " + error);
                    Thread.Sleep(1000);
                }
            }
        }

        public void Dispose()
        {
            if (stopped) return;
            stopped = true;
            listener.Stop();
            Session holder;
            lock (leaseGate) holder = leaseHolder;
            holder?.Abort();
        }

        private sealed class Session
        {
            private readonly RunnerServer server;
            private readonly Stream connection;
            private readonly CancellationTokenSource canceled = new CancellationTokenSource();
            private EventWriter writer;
            private string sessionId;
            private Lease lease;
            private Assembly testAssembly;
            private ITestBridge tests;
            private Dictionary<string, TestDescriptor> discovered;
            private string generation;
            private Task running;
            private Timer heartbeat;

            internal Session(RunnerServer server, Stream connection)
            {
                this.server = server;
                this.connection = connection;
            }

            private RunnerEnvironment Environment => server.environment;

            internal async Task Serve()
            {
                try {
                    var hello = await ReadHello().ConfigureAwait(false);
                    if (hello == null) return;
                    sessionId = hello.SessionId;
                    writer = new EventWriter(connection, sessionId);
                    await writer.WriteAsync(Welcome()).ConfigureAwait(false);
                    heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                    while (true) {
                        var request = await MessageFraming.ReadAsync(connection).ConfigureAwait(false);
                        if (request == null) break;
                        if (request.SessionId != sessionId) throw new ProtocolException(ProtocolException.OutOfOrder, "Request for another session.");
                        switch (request) {
                            case Lease leaseRequest: await HandleLease(leaseRequest).ConfigureAwait(false); break;
                            case Discover _: await HandleDiscover().ConfigureAwait(false); break;
                            case Run run:
                                if (running != null) throw new ProtocolException(ProtocolException.OutOfOrder, "A session runs once.");
                                running = Task.Run(() => HandleRun(run));
                                break;
                            case Cancel cancel:
                                server.Log?.Invoke("Cancel requested: " + cancel.Reason);
                                canceled.Cancel();
                                break;
                            default: throw new ProtocolException(ProtocolException.OutOfOrder, "Unexpected request " + request.Type);
                        }
                    }
                    // The client closed its side; a run in progress has nobody to report to.
                    canceled.Cancel();
                    if (running != null) await running.ConfigureAwait(false);
                }
                catch (Exception error) {
                    server.Log?.Invoke("Session ended: " + error);
                    canceled.Cancel();
                }
                finally {
                    heartbeat?.Dispose();
                    Release();
                    connection.Dispose();
                    server.SessionClosed?.Invoke();
                }
            }

            internal void Abort()
            {
                canceled.Cancel();
                connection.Dispose();
            }

            private async Task<Hello> ReadHello()
            {
                var read = MessageFraming.ReadAsync(connection);
                if (await Task.WhenAny(read, Task.Delay(HandshakeTimeout)).ConfigureAwait(false) != read) return null;
                // The token is compared before anything about this process is revealed.
                if (!(await read.ConfigureAwait(false) is Hello hello) || hello.Token != server.advertisement.Token || string.IsNullOrEmpty(hello.SessionId)) return null;
                return hello;
            }

            private Welcome Welcome() => new Welcome {
                HostVersion = RunnerEnvironment.HostVersion, Mode = Environment.Mode, RuntimeKind = Environment.RuntimeKind,
                ProcessId = server.advertisement.ProcessId, ProcessStartTime = server.advertisement.ProcessStartTime,
                GameVersion = Environment.GameVersion, UnityVersion = Environment.UnityVersion, InstallRoot = Environment.InstallRoot,
                MainThreadId = Environment.MainThreadId, Readiness = Environment.Readiness, Dirty = Environment.Dirty,
                LoadedModules = Environment.LoadedModules().ToList()
            };

            private void Heartbeat()
            {
                try { writer.WriteAsync(new Heartbeat { FrameCount = Environment.FrameCount }).GetAwaiter().GetResult(); }
                catch (Exception) { heartbeat?.Dispose(); }
            }

            private async Task HandleLease(Lease request)
            {
                if (lease != null) throw new ProtocolException(ProtocolException.OutOfOrder, "The session already holds a lease.");
                LeaseResult refusal = null;
                if (Environment.Dirty != null)
                    refusal = new LeaseResult { Reason = LeaseResult.Dirty, Detail = "Earlier work left this process unusable: " + Environment.Dirty + ". Restart it." };
                else if (request.Kind != TestDescriptor.TestKind && request.Kind != TestDescriptor.FuzzKind)
                    refusal = new LeaseResult { Reason = LeaseResult.Invalid, Detail = "Unsupported workload: " + (request.Kind ?? "none") };
                else {
                    lock (server.leaseGate) {
                        if (server.leaseHolder != null) refusal = new LeaseResult { Reason = LeaseResult.SessionBusy, Detail = "Another test session is running in this process." };
                        else server.leaseHolder = this;
                    }
                }
                if (refusal == null) {
                    var notReady = Environment.WaitUntilReady(TimeSpan.FromSeconds(request.ReadyTimeoutSeconds > 0 ? request.ReadyTimeoutSeconds : 300), canceled.Token);
                    if (notReady != null) refusal = new LeaseResult { Reason = LeaseResult.NotReady, Detail = notReady };
                }
                if (refusal == null) {
                    try {
                        testAssembly = Environment.LoadTestAssembly(request, out refusal);
                    }
                    catch (Exception error) {
                        refusal = new LeaseResult { Reason = LeaseResult.Invalid, Detail = "The test assembly could not be loaded: " + error };
                    }
                    if (refusal != null) Release();
                }
                if (refusal != null) {
                    var truncated = false;
                    refusal.Detail = MessageText.Limit(refusal.Detail, ref truncated);
                    await writer.WriteAsync(refusal).ConfigureAwait(false);
                    return;
                }
                lease = request;
                Environment.SessionStarting(canceled.Token);
                await writer.WriteAsync(new LeaseResult { Granted = true }).ConfigureAwait(false);
            }

            private async Task HandleDiscover()
            {
                if (lease == null) throw new ProtocolException(ProtocolException.OutOfOrder, "Discover requires a lease.");
                var descriptors = DiscoverTests();
                discovered = descriptors.ToDictionary(test => test.Id, StringComparer.Ordinal);
                generation = Guid.NewGuid().ToString("N");
                foreach (var diagnostic in tests.Diagnostics) await Write(new Log { Level = "diagnostic", Text = diagnostic }).ConfigureAwait(false);
                // Batches keep each message far below the frame limit.
                for (int start = 0; ; start += 200) {
                    var batch = descriptors.Skip(start).Take(200).ToList();
                    var final = start + 200 >= descriptors.Count;
                    await writer.WriteAsync(new TestsDiscovered { DiscoveryGeneration = generation, Tests = batch, Final = final }).ConfigureAwait(false);
                    if (final) break;
                }
            }

            private IReadOnlyList<TestDescriptor> DiscoverTests()
            {
                tests = Environment.CreateBridge(testAssembly, lease.Kind);
                return tests.Discover();
            }

            private async Task HandleRun(Run request)
            {
                var final = new SessionFinished { Discovered = discovered?.Count ?? 0, Cleanup = SessionFinished.Clean };
                try {
                    if (lease == null || discovered == null) throw new InvalidOperationException("Run requires a lease and a discovery.");
                    if (request.DiscoveryGeneration != generation) throw new InvalidOperationException("The run refers to a different discovery; discover again.");
                    var ids = request.TestIds ?? new List<string>();
                    var unknown = ids.Where(id => !discovered.ContainsKey(id)).ToList();
                    if (unknown.Count > 0) throw new InvalidOperationException("Unknown test ids: " + string.Join(", ", unknown.Take(10)));
                    // The client chose these for this mode; running a case declared for another mode would be a client bug.
                    var foreign = ids.Where(id => !discovered[id].RunsIn(Environment.Mode)).ToList();
                    if (foreign.Count > 0) throw new InvalidOperationException("Cases not declared for " + Environment.Mode + ": " + string.Join(", ", foreign.Take(10).Select(id => discovered[id].DisplayName)));
                    if (ids.Count > 0) {
                        var timeout = TimeSpan.FromSeconds(lease.TestTimeoutSeconds > 0 ? lease.TestTimeoutSeconds : 60);
                        var counts = await tests.Run(ids, request, Environment, writer, timeout, canceled.Token).ConfigureAwait(false);
                        final.Passed = counts.Passed;
                        final.Failed = counts.Failed;
                        final.Skipped = counts.Skipped;
                        final.Error = counts.Error;
                    }
                }
                catch (Exception error) {
                    final.Error = error.Message;
                }
                if (Environment.Dirty != null) {
                    final.Cleanup = SessionFinished.Dirty;
                    final.Error = (final.Error == null ? "" : final.Error + "\n") + "Dirty: " + Environment.Dirty;
                }
                try { final.Environment = Environment.EnvironmentReport(lease); }
                catch (Exception error) { final.Error = (final.Error == null ? "" : final.Error + "\n") + "Environment report failed: " + error.Message; }
                var truncated = false;
                final.Error = MessageText.Limit(final.Error, ref truncated);
                try { await writer.WriteAsync(final).ConfigureAwait(false); }
                catch (Exception error) { server.Log?.Invoke("SessionFinished was not delivered: " + error.Message); }
                Release();
            }

            private Task Write(Event message) => writer.WriteAsync(message);

            private void Release()
            {
                lock (server.leaseGate) {
                    if (server.leaseHolder != this) return;
                    server.leaseHolder = null;
                }
                try { Environment.SessionEnded(); }
                catch (Exception error) { server.Log?.Invoke("Session cleanup failed: " + error.Message); }
            }
        }
    }
}
