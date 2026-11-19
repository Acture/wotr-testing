using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WotR.Testing.Offline
{
    /// <summary>Watches logs produced while a rule runs. Swallowed errors fail the scope; the game's own catch stays as it is.</summary>
    public static class RuleScope
    {
        public static RuleScopeResult Run(string operation, Action action, params RuleLogAllowance[] allowances)
        {
            if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("An operation name is required.", nameof(operation));
            if (action == null) throw new ArgumentNullException(nameof(action));
            allowances ??= Array.Empty<RuleLogAllowance>();
            foreach (var allowance in allowances)
            {
                if (allowance == null) throw new ArgumentNullException(nameof(allowances));
                if (string.IsNullOrWhiteSpace(allowance.Reason)) throw new ArgumentException("An allowance needs a reason.", nameof(allowances));
                if (allowance.Match == null) throw new ArgumentException("An allowance needs a match.", nameof(allowances));
            }

            var unseededBefore = GameRandom.UnseededConstructions;
            var start = HarnessState.Logs.Count;
            action();
            var fresh = HarnessState.Logs.ToArray().Skip(start).ToArray();
            var allowed = new List<AllowedRuleLog>();
            var errors = new List<CapturedLog>();
            for (var i = 0; i < fresh.Length; i++)
            {
                var log = fresh[i];
                if (!IsError(log)) continue;
                var match = allowances.FirstOrDefault(allowance => allowance.Match(log));
                // Owlcat's LogChannel.Exception logs a header without the exception, then the exception; an allowed
                // exception allows its header.
                if (match == null && IsExceptionHeader(log, i + 1 < fresh.Length ? fresh[i + 1] : null))
                    match = allowances.FirstOrDefault(allowance => allowance.Match(fresh[i + 1]));
                if (match != null) allowed.Add(new AllowedRuleLog(match.Reason, log));
                else errors.Add(log);
            }
            if (errors.Count > 0) throw new RuleSettlementException(operation, errors);
            return new RuleScopeResult(allowed, GameRandom.UnseededConstructions - unseededBefore);
        }

        internal static bool IsError(CapturedLog log) => log.Severity is "Error" or "Exception" || log.Exception != null;

        /// <summary>The header LogChannel.Exception logs before an exception it has no message for.</summary>
        private static bool IsExceptionHeader(CapturedLog log, CapturedLog next)
            => next != null && log.Exception == null && next.Exception != null && log.Source == next.Source && log.Channel == next.Channel
                && log.Message == "Exception occured!";
    }

    public sealed class RuleLogAllowance
    {
        public string Reason { get; }
        public Func<CapturedLog, bool> Match { get; }

        public RuleLogAllowance(string reason, Func<CapturedLog, bool> match)
        {
            Reason = reason;
            Match = match;
        }
    }

    public sealed class AllowedRuleLog
    {
        public string Reason { get; }
        public CapturedLog Log { get; }

        internal AllowedRuleLog(string reason, CapturedLog log)
        {
            Reason = reason;
            Log = log;
        }
    }

    public sealed class RuleScopeResult
    {
        public IReadOnlyList<AllowedRuleLog> Allowed { get; }
        public long UnseededSystemRandom { get; }

        internal RuleScopeResult(IReadOnlyList<AllowedRuleLog> allowed, long unseededSystemRandom)
        {
            Allowed = allowed;
            UnseededSystemRandom = unseededSystemRandom;
        }
    }

    public sealed class RuleSettlementException : Exception
    {
        public string Operation { get; }
        public IReadOnlyList<CapturedLog> Errors { get; }

        public RuleSettlementException(string operation, IReadOnlyList<CapturedLog> errors)
            : base(Describe(operation, errors))
        {
            Operation = operation;
            Errors = errors;
        }

        private static string Describe(string operation, IReadOnlyList<CapturedLog> errors)
        {
            var text = new StringBuilder();
            text.Append("Rule settlement failed: ").Append(operation);
            foreach (var error in errors)
            {
                text.AppendLine();
                text.Append(Describe(error));
            }
            return text.ToString();
        }

        /// <summary>One logged error: its source, severity, channel, message and exception.</summary>
        internal static string Describe(CapturedLog error)
        {
            var text = new StringBuilder();
            text.Append(error.Source).Append(' ').Append(error.Severity);
            if (!string.IsNullOrEmpty(error.Channel)) text.Append(' ').Append(error.Channel);
            if (!string.IsNullOrEmpty(error.Message)) text.Append(": ").Append(error.Message);
            if (!string.IsNullOrEmpty(error.Exception))
            {
                text.AppendLine();
                text.Append(error.Exception);
            }
            return text.ToString();
        }
    }
}
