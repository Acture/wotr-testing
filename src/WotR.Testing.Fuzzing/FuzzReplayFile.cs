using System;
using System.IO;
using System.Text.Json;

namespace WotR.Testing.Fuzzing
{
    /// <summary>A saved case for command-line replay of a registered <see cref="FuzzTarget"/>.</summary>
    public sealed class FuzzReplayFile
    {
        public const int CurrentFormatVersion = 1;
        public const string OffGame = "offgame";
        public const string InGame = "ingame";

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        /// <summary>The <see cref="FuzzTarget.Id"/> to replay.</summary>
        public string Target { get; set; }
        /// <summary><see cref="OffGame"/> or <see cref="InGame"/>.</summary>
        public string Mode { get; set; }
        /// <summary>The serialized case from <see cref="FuzzReport.ReplayCase"/>, kept as exact text.</summary>
        public string Case { get; set; }

        /// <summary>Reads and validates a replay file; throws <see cref="InvalidDataException"/> for malformed content.</summary>
        public static FuzzReplayFile Read(string path)
        {
            RequirePath(path);
            var text = File.ReadAllText(path);
            Stored stored;
            try { stored = JsonSerializer.Deserialize<Stored>(text); }
            catch (JsonException exception) { throw new InvalidDataException("Replay file " + path + " is not a valid replay JSON object: " + exception.Message, exception); }
            if (stored == null) throw new InvalidDataException("Replay file " + path + " does not contain a JSON object.");
            if (stored.FormatVersion == null) throw new InvalidDataException("Replay file " + path + ": FormatVersion is missing.");
            var file = new FuzzReplayFile { FormatVersion = stored.FormatVersion.Value, Target = stored.Target, Mode = stored.Mode, Case = stored.Case };
            var problem = file.Problem();
            if (problem != null) throw new InvalidDataException("Replay file " + path + ": " + problem + ".");
            return file;
        }

        /// <summary>Writes the file after the same validation as <see cref="Read"/>; throws <see cref="InvalidOperationException"/> when incomplete.</summary>
        public void Write(string path)
        {
            RequirePath(path);
            var problem = Problem();
            if (problem != null) throw new InvalidOperationException("Cannot write replay file " + path + ": " + problem + ".");
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonFormat.Options));
        }

        private string Problem()
        {
            if (FormatVersion != CurrentFormatVersion) return "unsupported FormatVersion " + FormatVersion + "; expected " + CurrentFormatVersion;
            if (string.IsNullOrWhiteSpace(Target)) return "Target (the fuzz target Id) is required";
            if (Mode != OffGame && Mode != InGame) return "Mode must be \"" + OffGame + "\" or \"" + InGame + "\"" + (Mode == null ? "" : ", not \"" + Mode + "\"");
            if (string.IsNullOrWhiteSpace(Case)) return "Case (the serialized replay case) is required";
            try
            {
                using (var json = JsonDocument.Parse(Case))
                    if (json.RootElement.ValueKind != JsonValueKind.Object) return "Case must be a serialized replay case JSON object";
            }
            catch (JsonException) { return "Case is not valid JSON"; }
            return null;
        }

        private static void RequirePath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Trim().Length == 0) throw new ArgumentException("A replay file path is required.", nameof(path));
        }

        private sealed class Stored
        {
            public int? FormatVersion { get; set; }
            public string Target { get; set; }
            public string Mode { get; set; }
            public string Case { get; set; }
        }
    }
}
