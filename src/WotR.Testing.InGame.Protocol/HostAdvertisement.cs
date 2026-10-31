using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>
    /// How a running host can be reached. Stored under the current user's local application data, so the token is
    /// readable only by that user. A file proves nothing about compatibility; the handshake does.
    /// </summary>
    [DataContract]
    public sealed class HostAdvertisement
    {
        [DataMember(Name = "protocolVersion", Order = 0)] public int ProtocolVersion { get; set; }
        [DataMember(Name = "processId", Order = 1)] public int ProcessId { get; set; }
        [DataMember(Name = "processStartTime", Order = 2)] public string ProcessStartTime { get; set; }
        /// <summary>Loopback TCP port; the host listens on 127.0.0.1 only.</summary>
        [DataMember(Name = "port", Order = 3)] public int Port { get; set; }
        [DataMember(Name = "token", Order = 4)] public string Token { get; set; }
        [DataMember(Name = "hostVersion", Order = 5)] public string HostVersion { get; set; }
        [DataMember(Name = "mode", Order = 6)] public string Mode { get; set; }
        [DataMember(Name = "installRoot", Order = 7)] public string InstallRoot { get; set; }
        [DataMember(Name = "gameVersion", Order = 8)] public string GameVersion { get; set; }

        public static string Directory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WotR.Testing", "hosts");

        /// <summary>The advertisement for the current process, with a fresh token; the server sets the port.</summary>
        public static HostAdvertisement ForCurrentProcess(string mode, string hostVersion, string installRoot, string gameVersion)
        {
            using (var process = Process.GetCurrentProcess())
                return new HostAdvertisement {
                    ProtocolVersion = Message.CurrentVersion, ProcessId = process.Id, ProcessStartTime = Format(process.StartTime),
                    Token = Secret(32), HostVersion = hostVersion,
                    Mode = mode, InstallRoot = installRoot, GameVersion = gameVersion
                };
        }

        public string FilePath => Path.Combine(Directory, ProcessId.ToString(CultureInfo.InvariantCulture) + ".json");

        public void Write()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temporary = FilePath + "." + Secret(4) + ".tmp";
            File.WriteAllText(temporary, ProtocolJson.Serialize(this));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temporary, FilePath);
        }

        public void Remove()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (IOException) { /* A later host or client removes stale files. */ }
        }

        /// <summary>Advertisements of processes that still run with the advertised start time; stale files are removed.</summary>
        public static IReadOnlyList<HostAdvertisement> ReadLive()
        {
            var live = new List<HostAdvertisement>();
            if (!System.IO.Directory.Exists(Directory)) return live;
            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json")) {
                HostAdvertisement advertisement;
                try { advertisement = ProtocolJson.Deserialize<HostAdvertisement>(File.ReadAllText(path)); }
                catch (Exception error) when (error is IOException || error is ProtocolException) { continue; }
                if (advertisement.IsAlive()) live.Add(advertisement);
                else {
                    try { File.Delete(path); }
                    catch (IOException) { }
                }
            }
            return live.OrderBy(item => item.ProcessId).ToList();
        }

        /// <summary>The advertised process still exists; a reused PID has a different start time.</summary>
        public bool IsAlive() => ProcessStartTime != null && StartTimeOf(ProcessId) is DateTime started && SameTime(started, ProcessStartTime);

        public static DateTime? StartTimeOf(int processId)
        {
            try {
                using (var process = Process.GetProcessById(processId))
                    return process.HasExited ? (DateTime?)null : process.StartTime;
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception) { return null; }
        }

        public static string Format(DateTime time) => time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // Runtimes report the start time with different precision.
        public static bool SameTime(DateTime actual, string advertised)
            => DateTime.TryParse(advertised, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
               && Math.Abs((actual.ToUniversalTime() - parsed.ToUniversalTime()).TotalSeconds) < 1;

        public static string Secret(int bytes)
        {
            var value = new byte[bytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(value);
            return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        }
    }
}
