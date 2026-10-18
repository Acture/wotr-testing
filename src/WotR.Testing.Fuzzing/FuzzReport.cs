using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotR.Testing.Fuzzing
{
    public enum FuzzStatus { Passed, Finding, InfrastructureFailure, AllSkipped }

    public sealed class FuzzReport
    {
        public bool Succeeded => Status == FuzzStatus.Passed;
        public FuzzStatus Status { get; internal set; }
        public int Seed { get; internal set; }
        public int CasesAttempted { get; internal set; }
        public int CasesPassed { get; internal set; }
        public int CasesSkipped { get; internal set; }
        public int CallsAttempted { get; internal set; }
        public int CallsCompleted { get; internal set; }
        public int ExpectedExceptions { get; internal set; }
        public int ShrinkAttempts { get; internal set; }
        public FuzzFinding Failure { get; internal set; }
        public string ReplayCase { get; internal set; }
        public IReadOnlyList<string> SelectedMethods { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> SkippedReasons { get; internal set; } = Array.Empty<string>();

        public string ToJson() => JsonSerializer.Serialize(this, JsonFormat.Options);
    }

    public sealed class FuzzFinding
    {
        /// <summary>invocation, factory, dispose, generation, serialization, or classifier.</summary>
        public string Kind { get; internal set; }
        public string Method { get; internal set; }
        public string ExceptionType { get; internal set; }
        public string Message { get; internal set; }
        public string ExceptionDetails { get; internal set; }
    }

    public sealed class FuzzFailureException : Exception
    {
        public FuzzReport Report { get; }

        public FuzzFailureException(FuzzReport report)
            : base(Describe(report)) { Report = report; }

        private static string Describe(FuzzReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            return "Interface fuzzing: " + report.Status + ". " +
                (report.Failure == null ? report.CasesSkipped + " unavailable cases; no case passed." :
                    report.Failure.Kind + " " + report.Failure.Method + ": " + report.Failure.ExceptionType + ": " + report.Failure.Message) +
                " Seed=" + report.Seed + "." +
                (report.ReplayCase == null ? " Inspect Report for details." : "\nReplay case (save this JSON for Fuzz.Replay):\n" + report.ReplayCase);
        }
    }

    internal static class JsonFormat
    {
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
