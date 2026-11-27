using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WotR.Testing.Offline
{
    /// <summary>
    /// Serves a <see cref="BattleEnvironment"/> to a training program in another process, for example Python with an
    /// off-the-shelf trainer: one client on a local TCP port, one JSON object per line each way. The battle runs in the
    /// test process that calls <see cref="Serve"/>, inside the offline runtime; the client only sends decisions.
    /// </summary>
    /// <remarks>
    /// The conversation itself is <see cref="BattleEnvironmentProtocol"/>: a consumer that drives the battle in its own
    /// process speaks it without a socket. See examples/training for a client.
    /// </remarks>
    public static class BattleEnvironmentServer
    {
        /// <summary>
        /// Serves one client on the loopback port until it sends <c>close</c> or disconnects. <paramref name="scenario"/>
        /// builds the scenario a <c>start</c> request names, turn-based or not.
        /// </summary>
        public static void Serve(int port, Func<string, bool, BattleScenario> scenario)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            using var protocol = new BattleEnvironmentProtocol(scenario);
            try
            {
                using var client = listener.AcceptTcpClient();
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                string line;
                while (!protocol.Closed && (line = reader.ReadLine()) != null)
                {
                    writer.WriteLine(protocol.Handle(line));
                }
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
