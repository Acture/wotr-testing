using System;
using System.Collections.Generic;
using System.Linq;

namespace WotR.Testing.InGame.Protocol
{
    /// <summary>
    /// Client-side record of one session's events. Rejects events that break the stream's order; <see cref="Problems"/>
    /// lists what prevents trusting the results. Failed and skipped tests are results, not problems.
    /// </summary>
    public sealed class SessionTracker
    {
        private static readonly string[] Outcomes = { TestFinished.Passed, TestFinished.Failed, TestFinished.Skipped };
        private readonly string sessionId;
        private readonly HashSet<string> running = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TestFinished> results = new Dictionary<string, TestFinished>(StringComparer.Ordinal);
        private readonly List<TestFinished> order = new List<TestFinished>();
        private long sequence;

        public SessionTracker(string sessionId) => this.sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));

        public Welcome Host { get; private set; }
        public Heartbeat LastHeartbeat { get; private set; }
        public SessionFinished Final { get; private set; }
        public IReadOnlyList<TestFinished> Results => order;

        public void Accept(Message message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (!(message is Event received)) throw Violation("The host sent a client request: " + message.Type);
            if (message.SessionId != sessionId) throw Violation("Event for another session: " + (message.SessionId ?? "none"));
            if (Final != null) throw Violation(message.Type + " arrived after SessionFinished.");
            if (received.Sequence != sequence + 1) throw Violation("Expected event " + (sequence + 1) + ", received " + received.Sequence + " (" + message.Type + ").");
            if (Host == null && !(received is Welcome)) throw Violation("The first event must be Welcome, not " + message.Type + ".");
            sequence = received.Sequence;
            switch (received) {
                case Welcome welcome:
                    if (Host != null) throw Violation("Duplicate Welcome.");
                    Host = welcome;
                    break;
                case TestStarted started:
                    if (string.IsNullOrEmpty(started.TestId)) throw Violation("TestStarted without a test id.");
                    if (results.ContainsKey(started.TestId) || !running.Add(started.TestId)) throw Violation("Test started twice: " + started.TestId);
                    break;
                case TestFinished finished:
                    if (finished.TestId == null || !running.Remove(finished.TestId)) throw Violation("TestFinished without TestStarted: " + finished.TestId);
                    if (!Outcomes.Contains(finished.Outcome)) throw Violation("Unknown outcome " + (finished.Outcome ?? "none") + " for " + finished.TestId);
                    results.Add(finished.TestId, finished);
                    order.Add(finished);
                    break;
                case Heartbeat heartbeat:
                    LastHeartbeat = heartbeat;
                    break;
                case SessionFinished final:
                    Final = final;
                    break;
            }
        }

        public IReadOnlyList<string> Problems()
        {
            var problems = new List<string>();
            if (Final == null) problems.Add("Incomplete session: the host did not send SessionFinished.");
            else {
                if (!string.IsNullOrEmpty(Final.Error)) problems.Add("Host error: " + Final.Error);
                if (Final.Cleanup != SessionFinished.Clean) problems.Add("Session cleanup is " + (Final.Cleanup ?? "unreported") + "; the player cannot be reused until it is reset.");
                var passed = order.Count(result => result.Outcome == TestFinished.Passed);
                var failed = order.Count(result => result.Outcome == TestFinished.Failed);
                var skipped = order.Count(result => result.Outcome == TestFinished.Skipped);
                if (Final.Passed != passed || Final.Failed != failed || Final.Skipped != skipped)
                    problems.Add($"SessionFinished reports {Final.Passed} passed, {Final.Failed} failed, {Final.Skipped} skipped; the stream contained {passed}, {failed}, {skipped}.");
            }
            problems.AddRange(running.Select(id => "Test started but did not finish: " + id));
            problems.AddRange(order.Where(result => result.Cleanup != SessionFinished.Clean)
                .Select(result => "Cleanup after " + result.TestId + " is " + (result.Cleanup ?? "unreported") + "."));
            return problems;
        }

        private static ProtocolException Violation(string message) => new ProtocolException(ProtocolException.OutOfOrder, message);
    }
}
