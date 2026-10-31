using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>Fields of every message. The wire type is the concrete class name.</summary>
    [DataContract]
    public abstract class Message
    {
        public const int CurrentVersion = 1;
        // The serializer does not run initializers, so a message without a version reads as 0 and is rejected.
        [DataMember(Name = "protocolVersion", Order = 0)] public int ProtocolVersion { get; set; } = CurrentVersion;
        [DataMember(Name = "type", Order = 1)] public string Type { get; set; }
        [DataMember(Name = "sessionId", Order = 2)] public string SessionId { get; set; }
        [DataMember(Name = "requestId", Order = 3)] public string RequestId { get; set; }

        internal static readonly Dictionary<string, Type> Types = new[] {
            typeof(Hello), typeof(Lease), typeof(Discover), typeof(Run), typeof(Cancel),
            typeof(Welcome), typeof(LeaseResult), typeof(TestsDiscovered), typeof(TestStarted), typeof(TestFinished),
            typeof(Log), typeof(Heartbeat), typeof(SessionFinished)
        }.ToDictionary(type => type.Name, StringComparer.Ordinal);
    }

    /// <summary>Host-to-client message. One writer numbers them from 1 so loss, duplication and reordering are visible.</summary>
    [DataContract]
    public abstract class Event : Message
    {
        [DataMember(Name = "sequence", Order = 10)] public long Sequence { get; set; }
    }

    [DataContract]
    internal sealed class Envelope : Message { }

    [DataContract]
    public sealed class ModuleIdentity
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "mvid", Order = 1)] public string Mvid { get; set; }
    }

    [DataContract]
    public sealed class TestTrait
    {
        [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
        [DataMember(Name = "value", Order = 1)] public string Value { get; set; }
    }

    /// <summary>A discovered case or fuzz target. The id is opaque and only valid for its discovery generation.</summary>
    [DataContract]
    public sealed class TestDescriptor
    {
        public const string TestKind = "tests";
        public const string FuzzKind = "fuzz";
        /// <summary>The trait naming where a case runs: offgame (also when absent), ingame or both.</summary>
        public const string ModeTrait = "WotrMode";
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "displayName", Order = 1)] public string DisplayName { get; set; }
        [DataMember(Name = "fullyQualifiedName", Order = 2)] public string FullyQualifiedName { get; set; }
        [DataMember(Name = "traits", Order = 3)] public List<TestTrait> Traits { get; set; }
        [DataMember(Name = "kind", Order = 4)] public string Kind { get; set; }
        [DataMember(Name = "skipReason", Order = 5)] public string SkipReason { get; set; }
        [DataMember(Name = "sourceFile", Order = 6)] public string SourceFile { get; set; }
        [DataMember(Name = "sourceLine", Order = 7)] public int SourceLine { get; set; }

        /// <summary>Whether the case declares <paramref name="mode"/>; a case without the trait is offgame only.</summary>
        public bool RunsIn(string mode)
        {
            var declared = Traits?.Where(trait => trait.Name == ModeTrait).Select(trait => trait.Value).ToList() ?? new List<string>();
            if (declared.Count == 0) return mode == Welcome.OffGame;
            return declared.Any(value => value == "both" || value == mode);
        }
    }

    // Client requests.

    [DataContract]
    public sealed class Hello : Message
    {
        [DataMember(Name = "clientVersion", Order = 20)] public string ClientVersion { get; set; }
        [DataMember(Name = "runId", Order = 21)] public string RunId { get; set; }
        /// <summary>The secret from the host's advertisement, readable only by the same Windows user.</summary>
        [DataMember(Name = "token", Order = 22)] public string Token { get; set; }
    }

    /// <summary>Takes the host's single test session for one bundle; the host checks the expected live modules first.</summary>
    [DataContract]
    public sealed class Lease : Message
    {
        [DataMember(Name = "testAssembly", Order = 20)] public string TestAssembly { get; set; }
        [DataMember(Name = "bundleHash", Order = 21)] public string BundleHash { get; set; }
        [DataMember(Name = "expectedModules", Order = 22)] public List<ModuleIdentity> ExpectedModules { get; set; }
        /// <summary><see cref="TestDescriptor.TestKind"/> or <see cref="TestDescriptor.FuzzKind"/>.</summary>
        [DataMember(Name = "kind", Order = 23)] public string Kind { get; set; }
        [DataMember(Name = "reportDirectory", Order = 24)] public string ReportDirectory { get; set; }
        [DataMember(Name = "testTimeoutSeconds", Order = 25)] public int TestTimeoutSeconds { get; set; }
        /// <summary>
        /// Extra files copied beside the test assembly: framework facades the player lacks but test libraries
        /// reference (they only forward types to the player's own class library).
        /// </summary>
        [DataMember(Name = "supportFiles", Order = 26)] public List<string> SupportFiles { get; set; }
        /// <summary>How long the host may wait for the game to finish loading before it refuses the lease.</summary>
        [DataMember(Name = "readyTimeoutSeconds", Order = 27)] public int ReadyTimeoutSeconds { get; set; }
    }

    [DataContract]
    public sealed class Discover : Message { }

    [DataContract]
    public sealed class Run : Message
    {
        [DataMember(Name = "discoveryGeneration", Order = 20)] public string DiscoveryGeneration { get; set; }
        [DataMember(Name = "testIds", Order = 21)] public List<string> TestIds { get; set; }
        /// <summary>Overrides the seed of fuzz targets.</summary>
        [DataMember(Name = "fuzzSeed", Order = 22)] public int? FuzzSeed { get; set; }
        /// <summary>A saved case to replay exactly, for the single selected fuzz target.</summary>
        [DataMember(Name = "fuzzReplayCase", Order = 23)] public string FuzzReplayCase { get; set; }
    }

    /// <summary>Cooperative: it cannot stop game code that is already blocked.</summary>
    [DataContract]
    public sealed class Cancel : Message
    {
        [DataMember(Name = "reason", Order = 20)] public string Reason { get; set; }
    }

    // Host events.

    /// <summary>The host's identity. Advertised modules describe what the process loaded, not files on disk.</summary>
    [DataContract]
    public sealed class Welcome : Event
    {
        public const string InGame = "ingame";
        public const string OffGame = "offgame";
        [DataMember(Name = "hostVersion", Order = 20)] public string HostVersion { get; set; }
        /// <summary><see cref="InGame"/> for the game's player, <see cref="OffGame"/> for an offline worker.</summary>
        [DataMember(Name = "mode", Order = 21)] public string Mode { get; set; }
        /// <summary>unity-player, unity-mono or netfx.</summary>
        [DataMember(Name = "runtimeKind", Order = 22)] public string RuntimeKind { get; set; }
        [DataMember(Name = "processId", Order = 23)] public int ProcessId { get; set; }
        [DataMember(Name = "processStartTime", Order = 24)] public string ProcessStartTime { get; set; }
        [DataMember(Name = "gameVersion", Order = 25)] public string GameVersion { get; set; }
        [DataMember(Name = "unityVersion", Order = 26)] public string UnityVersion { get; set; }
        [DataMember(Name = "installRoot", Order = 27)] public string InstallRoot { get; set; }
        [DataMember(Name = "mainThreadId", Order = 28)] public int MainThreadId { get; set; }
        [DataMember(Name = "readiness", Order = 29)] public string Readiness { get; set; }
        /// <summary>Why earlier work left the process unusable, or null.</summary>
        [DataMember(Name = "dirty", Order = 30)] public string Dirty { get; set; }
        [DataMember(Name = "loadedModules", Order = 31)] public List<ModuleIdentity> LoadedModules { get; set; }
    }

    [DataContract]
    public sealed class LeaseResult : Event
    {
        public const string SessionBusy = "session-busy";
        public const string RestartRequired = "restart-required";
        public const string ModNotLoaded = "mod-not-loaded";
        public const string Dirty = "dirty";
        public const string Invalid = "invalid-request";
        public const string NotReady = "not-ready";
        [DataMember(Name = "granted", Order = 20)] public bool Granted { get; set; }
        [DataMember(Name = "reason", Order = 21)] public string Reason { get; set; }
        [DataMember(Name = "detail", Order = 22)] public string Detail { get; set; }
    }

    /// <summary>One batch of discovered cases; the last batch sets <see cref="Final"/>.</summary>
    [DataContract]
    public sealed class TestsDiscovered : Event
    {
        [DataMember(Name = "discoveryGeneration", Order = 20)] public string DiscoveryGeneration { get; set; }
        [DataMember(Name = "tests", Order = 21)] public List<TestDescriptor> Tests { get; set; }
        [DataMember(Name = "final", Order = 22)] public bool Final { get; set; }
    }

    /// <summary>A Theory whose rows are not enumerated at discovery starts once per row, each with its own display name.</summary>
    [DataContract]
    public sealed class TestStarted : Event
    {
        [DataMember(Name = "testId", Order = 20)] public string TestId { get; set; }
        [DataMember(Name = "displayName", Order = 21)] public string DisplayName { get; set; }
    }

    [DataContract]
    public sealed class TestFinished : Event
    {
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
        [DataMember(Name = "testId", Order = 20)] public string TestId { get; set; }
        [DataMember(Name = "outcome", Order = 21)] public string Outcome { get; set; }
        [DataMember(Name = "durationMilliseconds", Order = 22)] public long DurationMilliseconds { get; set; }
        [DataMember(Name = "message", Order = 23)] public string Message { get; set; }
        [DataMember(Name = "stackTrace", Order = 24)] public string StackTrace { get; set; }
        [DataMember(Name = "output", Order = 25)] public string Output { get; set; }
        [DataMember(Name = "truncated", Order = 26)] public bool Truncated { get; set; }
        [DataMember(Name = "cleanup", Order = 27)] public string Cleanup { get; set; }
        [DataMember(Name = "displayName", Order = 28)] public string DisplayName { get; set; }
        /// <summary>For fuzz targets: the saved case of a finding, for exact replay.</summary>
        [DataMember(Name = "replayCase", Order = 29)] public string ReplayCase { get; set; }
    }

    [DataContract]
    public sealed class Log : Event
    {
        [DataMember(Name = "level", Order = 20)] public string Level { get; set; }
        [DataMember(Name = "text", Order = 21)] public string Text { get; set; }
        [DataMember(Name = "truncated", Order = 22)] public bool Truncated { get; set; }
    }

    /// <summary>Sent from a background thread; <see cref="FrameCount"/> shows whether Unity is still advancing.</summary>
    [DataContract]
    public sealed class Heartbeat : Event
    {
        [DataMember(Name = "frameCount", Order = 20)] public long FrameCount { get; set; }
    }

    /// <summary>The only successful end of a session. Counts must match the streamed results.</summary>
    [DataContract]
    public sealed class SessionFinished : Event
    {
        public const string Clean = "clean";
        public const string Dirty = "dirty";
        [DataMember(Name = "discovered", Order = 20)] public int Discovered { get; set; }
        [DataMember(Name = "passed", Order = 21)] public int Passed { get; set; }
        [DataMember(Name = "failed", Order = 22)] public int Failed { get; set; }
        [DataMember(Name = "skipped", Order = 23)] public int Skipped { get; set; }
        [DataMember(Name = "cleanup", Order = 24)] public string Cleanup { get; set; }
        [DataMember(Name = "error", Order = 25)] public string Error { get; set; }
        /// <summary>The partition's environment report as JSON, written to its report directory by the client.</summary>
        [DataMember(Name = "environment", Order = 26)] public string Environment { get; set; }
    }

    public sealed class ProtocolException : Exception
    {
        public const string Disconnected = "disconnected";
        public const string InvalidLength = "invalid-length";
        public const string Malformed = "malformed";
        public const string Mismatch = "protocol-mismatch";
        public const string OutOfOrder = "out-of-order";
        public string Code { get; }
        public ProtocolException(string code, string message, Exception inner = null) : base("[" + code + "] " + message, inner) => Code = code;
    }

    public static class MessageText
    {
        /// <summary>Diagnostic text limit per field, well inside the frame limit.</summary>
        public const int MaxChars = 32 * 1024;

        /// <summary>Shortens oversized diagnostics; the caller records <paramref name="truncated"/> in the message.</summary>
        public static string Limit(string text, ref bool truncated)
        {
            if (text == null || text.Length <= MaxChars) return text;
            truncated = true;
            // Do not split a surrogate pair: half of one cannot be encoded as UTF-8.
            var length = char.IsHighSurrogate(text[MaxChars - 1]) ? MaxChars - 1 : MaxChars;
            return text.Substring(0, length);
        }
    }
}
