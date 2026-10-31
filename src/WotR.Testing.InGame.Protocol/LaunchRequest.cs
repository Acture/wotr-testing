using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>
    /// Left by a test run that starts the game, for the host in whichever process ends up running (Steam may replace
    /// the started one). Tells the host to keep the starting game out of the user's way.
    /// </summary>
    [DataContract]
    public sealed class LaunchRequest
    {
        public static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

        [DataMember(Name = "requested", Order = 0)] public string Requested { get; set; }
        /// <summary>Keep the game from taking the focus from the user's work while it loads.</summary>
        [DataMember(Name = "background", Order = 1)] public bool Background { get; set; }
        /// <summary>The window that had the focus when the run started the game, which gets it back.</summary>
        [DataMember(Name = "returnFocus", Order = 2)] public long ReturnFocus { get; set; }

        public static string FilePath => Path.Combine(Path.GetDirectoryName(HostAdvertisement.Directory), "launch-request.json");

        public static void Write(bool background, long returnFocus)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, ProtocolJson.Serialize(new LaunchRequest {
                Requested = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Background = background, ReturnFocus = returnFocus
            }));
        }

        public static void Remove()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (IOException) { }
        }

        /// <summary>The request of a launch that started within <see cref="Validity"/>, or null.</summary>
        public static LaunchRequest ReadRecent()
        {
            try {
                if (!File.Exists(FilePath)) return null;
                var request = ProtocolJson.Deserialize<LaunchRequest>(File.ReadAllText(FilePath));
                return DateTime.TryParse(request.Requested, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requested) &&
                       DateTime.UtcNow - requested.ToUniversalTime() < Validity ? request : null;
            }
            catch (Exception error) when (error is IOException || error is ProtocolException) { return null; }
        }
    }
}
