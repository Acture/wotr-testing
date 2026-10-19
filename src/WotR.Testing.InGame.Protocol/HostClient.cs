using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>
    /// One session with a host: Hello, Lease, Discover, then one Run, which ends with SessionFinished. Every received
    /// event passes through <see cref="Tracker"/>, so a broken stream fails instead of producing results.
    /// </summary>
    public sealed class HostClient : IDisposable
    {
        private readonly Stream stream;
        private readonly SemaphoreSlim writes = new SemaphoreSlim(1, 1);
        private int requests;

        private HostClient(Stream stream, string sessionId)
        {
            this.stream = stream;
            SessionId = sessionId;
            Tracker = new SessionTracker(sessionId);
        }

        public string SessionId { get; }
        public SessionTracker Tracker { get; }
        public Welcome Host => Tracker.Host;

        /// <summary>Longest silence, heartbeats included, before the host counts as unresponsive.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Logs and heartbeats as they arrive; called on the reading thread.</summary>
        public event Action<Event> Received;

        /// <summary>Connects to a host listening on the loopback interface and completes the handshake.</summary>
        public static async Task<HostClient> ConnectAsync(int port, string token, string runId, string clientVersion,
            TimeSpan connectTimeout, CancellationToken cancellationToken = default)
        {
            var tcp = new TcpClient { NoDelay = true };
            try {
                var connect = tcp.ConnectAsync(IPAddress.Loopback, port);
                if (await Task.WhenAny(connect, Task.Delay(connectTimeout, cancellationToken)).ConfigureAwait(false) != connect) {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("No connection to port " + port + " within " + connectTimeout.TotalSeconds + " seconds.");
                }
                await connect.ConfigureAwait(false);
                return await HandshakeAsync(tcp.GetStream(), token, runId, clientVersion, connectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch {
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>Starts a session on an already connected stream.</summary>
        public static async Task<HostClient> HandshakeAsync(Stream stream, string token, string runId, string clientVersion,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var client = new HostClient(stream, Guid.NewGuid().ToString("N")) { IdleTimeout = timeout };
            await client.SendAsync(new Hello { Token = token, RunId = runId, ClientVersion = clientVersion }, cancellationToken).ConfigureAwait(false);
            await client.ReadUntilAsync<Welcome>(cancellationToken).ConfigureAwait(false);
            client.IdleTimeout = TimeSpan.FromSeconds(30);
            return client;
        }

        public async Task<LeaseResult> LeaseAsync(Lease request, CancellationToken cancellationToken = default)
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadUntilAsync<LeaseResult>(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TestDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            await SendAsync(new Discover(), cancellationToken).ConfigureAwait(false);
            var tests = new List<TestDescriptor>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            string generation = null;
            while (true) {
                var batch = await ReadUntilAsync<TestsDiscovered>(cancellationToken).ConfigureAwait(false);
                if (generation != null && batch.DiscoveryGeneration != generation)
                    throw new ProtocolException(ProtocolException.OutOfOrder, "Discovery batches from different generations.");
                generation = DiscoveryGeneration = batch.DiscoveryGeneration;
                foreach (var test in batch.Tests ?? new List<TestDescriptor>()) {
                    if (string.IsNullOrEmpty(test?.Id) || !ids.Add(test.Id)) throw new ProtocolException(ProtocolException.Malformed, "Discovered case without a unique id.");
                    tests.Add(test);
                }
                if (batch.Final) return tests;
            }
        }

        public string DiscoveryGeneration { get; private set; }

        /// <summary>Runs the selected cases; an empty selection only ends the session. Results are in <see cref="Tracker"/>.</summary>
        public async Task<SessionFinished> RunAsync(Run request, CancellationToken cancellationToken = default)
        {
            request.DiscoveryGeneration = request.DiscoveryGeneration ?? DiscoveryGeneration;
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => _ = CancelAsync("The test run was canceled.")))
                return await ReadUntilAsync<SessionFinished>(CancellationToken.None).ConfigureAwait(false);
        }

        public async Task CancelAsync(string reason)
        {
            try { await SendAsync(new Cancel { Reason = reason }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is ProtocolException) { }
        }

        private async Task SendAsync(Message request, CancellationToken cancellationToken)
        {
            request.SessionId = SessionId;
            request.RequestId = Interlocked.Increment(ref requests).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await MessageFraming.WriteAsync(stream, request, cancellationToken).ConfigureAwait(false); }
            finally { writes.Release(); }
        }

        private async Task<T> ReadUntilAsync<T>(CancellationToken cancellationToken) where T : Event
        {
            while (true) {
                Message message;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                    idle.CancelAfter(IdleTimeout);
                    var read = MessageFraming.ReadAsync(stream, idle.Token);
                    // Pipe reads do not always observe cancellation; the delay bounds the wait regardless.
                    if (await Task.WhenAny(read, Task.Delay(IdleTimeout + TimeSpan.FromSeconds(1), cancellationToken)).ConfigureAwait(false) != read) {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new ProtocolException(ProtocolException.Disconnected, "The host sent nothing, not even a heartbeat, for " + IdleTimeout.TotalSeconds + " seconds.");
                    }
                    try { message = await read.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new ProtocolException(ProtocolException.Disconnected, "The host sent nothing, not even a heartbeat, for " + IdleTimeout.TotalSeconds + " seconds."); }
                }
                if (message == null) throw new ProtocolException(ProtocolException.Disconnected, "The host closed the connection before " + typeof(T).Name + ".");
                Tracker.Accept(message);
                var received = (Event)message;
                Received?.Invoke(received);
                if (received is T expected) return expected;
                if (received is SessionFinished final)
                    throw new ProtocolException(ProtocolException.OutOfOrder, "The host ended the session while " + typeof(T).Name + " was expected" +
                        (string.IsNullOrEmpty(final.Error) ? "." : ": " + final.Error));
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
