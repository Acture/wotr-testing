using System;

namespace WotR.OfflineTesting
{
    /// <summary>Prefixes that let the runner classify results from the standard TRX report.</summary>
    public static class OutcomeMarkers
    {
        public const string EnvironmentMissing = "[ENV_MISSING]";
        public const string InitializationFailed = "[INIT_FAILED]";
        public const string UnityRuntimeRequired = "[UNITY_RUNTIME_REQUIRED]";
    }

    /// <summary>The game inputs, mod build or a declared dependency are not available.</summary>
    public sealed class OfflineEnvironmentMissingException : Exception
    {
        public OfflineEnvironmentMissingException(string message, Exception inner = null)
            : base($"{OutcomeMarkers.EnvironmentMissing} {message}", inner) { }
    }

    /// <summary>A real game or mod initialization stage failed; reported as-is, never retried with substitutes.</summary>
    public sealed class OfflineInitializationException : Exception
    {
        public string Stage { get; }

        public OfflineInitializationException(string stage, string message, Exception inner = null)
            : base($"{OutcomeMarkers.InitializationFailed} {stage}: {message}", inner)
        {
            Stage = stage;
        }
    }

    /// <summary>Recognizes failures caused by reaching Unity engine code that does not exist offline.</summary>
    public static class UnityNative
    {
        public const string Marker = RuntimeAssemblyRewriter.NativeMarker;

        public static bool IsUnavailable(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
                if (e is NotSupportedException && e.Message.Contains(Marker)) return true;
            return false;
        }

        /// <summary>Skip reason for test frameworks: classified as unity-runtime-required, never as passed.</summary>
        public static string SkipReason(string what, Exception error)
            => $"{OutcomeMarkers.UnityRuntimeRequired} {what} reached {error.Message}";
    }
}
