using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;
using Xunit;

namespace WotR.Testing.InGame.Tests;

public class FramingTests
{
    [Fact]
    public async Task Round_trip_keeps_fields_and_documented_wire_names()
    {
        var run = new Run { SessionId = "s", RequestId = "r1", DiscoveryGeneration = "g1", TestIds = new List<string> { "a", "b" } };
        var frame = MessageFraming.Encode(run);
        Assert.Equal(frame.Length - 4, BitConverter.ToInt32(frame, 0));
        var json = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
        Assert.StartsWith("{\"protocolVersion\":1,\"type\":\"Run\",\"sessionId\":\"s\",\"requestId\":\"r1\"", json);
        var read = Assert.IsType<Run>(await MessageFraming.ReadAsync(new MemoryStream(frame)));
        Assert.Equal(new[] { "a", "b" }, read.TestIds);
        Assert.Equal("g1", read.DiscoveryGeneration);
        Assert.Equal("r1", read.RequestId);
    }

    [Fact]
    public async Task Partial_reads_assemble_complete_messages()
    {
        var bytes = MessageFraming.Encode(new Hello { SessionId = "s", RunId = "run" })
            .Concat(MessageFraming.Encode(new Cancel { SessionId = "s", Reason = "stop" })).ToArray();
        var stream = new TrickleStream(bytes);
        Assert.Equal("run", Assert.IsType<Hello>(await MessageFraming.ReadAsync(stream)).RunId);
        Assert.Equal("stop", Assert.IsType<Cancel>(await MessageFraming.ReadAsync(stream)).Reason);
        Assert.Null(await MessageFraming.ReadAsync(stream));
        Assert.True(stream.Reads > bytes.Length, "Every read returned at most one byte.");
    }

    [Fact]
    public async Task Two_messages_in_one_buffer_are_read_separately()
    {
        var stream = new MemoryStream(MessageFraming.Encode(new Discover { SessionId = "s", RequestId = "1" })
            .Concat(MessageFraming.Encode(new Discover { SessionId = "s", RequestId = "2" })).ToArray());
        Assert.Equal("1", (await MessageFraming.ReadAsync(stream)).RequestId);
        Assert.Equal("2", (await MessageFraming.ReadAsync(stream)).RequestId);
        Assert.Null(await MessageFraming.ReadAsync(stream));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(9)]
    public async Task Disconnect_inside_a_message_is_an_error_not_an_end(int keep)
    {
        var frame = MessageFraming.Encode(new Discover { SessionId = "s" });
        var error = await Assert.ThrowsAsync<ProtocolException>(() => MessageFraming.ReadAsync(new MemoryStream(frame.Take(keep).ToArray())));
        Assert.Equal(ProtocolException.Disconnected, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MessageFraming.MaxMessageBytes + 1)]
    public async Task Invalid_lengths_are_rejected_before_reading_a_payload(int length)
    {
        var error = await Assert.ThrowsAsync<ProtocolException>(() => MessageFraming.ReadAsync(new MemoryStream(BitConverter.GetBytes(length))));
        Assert.Equal(ProtocolException.InvalidLength, error.Code);
    }

    [Theory]
    [InlineData("{\"protocolVersion\":2,\"type\":\"Hello\"}", ProtocolException.Mismatch)]
    [InlineData("{\"type\":\"Hello\"}", ProtocolException.Mismatch)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Shutdown\"}", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1}", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Hello\"", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Run\",\"testIds\":[\"a\"}", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Log\",\"text\":\"}\"", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Hello\"}{}", ProtocolException.Malformed)]
    [InlineData("[1,2]", ProtocolException.Malformed)]
    [InlineData("null", ProtocolException.Malformed)]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Run\",\"testIds\":\"not a list\"}", ProtocolException.Malformed)]
    public async Task Invalid_versions_types_and_json_are_rejected(string json, string code)
    {
        var error = await Assert.ThrowsAsync<ProtocolException>(() => MessageFraming.ReadAsync(new MemoryStream(Frame(Encoding.UTF8.GetBytes(json)))));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Brackets_quotes_and_non_ascii_text_inside_strings_round_trip()
    {
        const string text = "}{ ] [ \"quoted\" \\ \\\" 中文 \U0001F600 \n end";
        var read = Assert.IsType<Log>(await MessageFraming.ReadAsync(new MemoryStream(MessageFraming.Encode(new Log { SessionId = "s", Text = text }))));
        Assert.Equal(text, read.Text);
    }

    [Fact]
    public async Task Invalid_utf8_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"type\":\"Log\",\"text\":\"xx\"}");
        bytes[Array.IndexOf(bytes, (byte)'x')] = 0xFF;
        var error = await Assert.ThrowsAsync<ProtocolException>(() => MessageFraming.ReadAsync(new MemoryStream(Frame(bytes))));
        Assert.Equal(ProtocolException.Malformed, error.Code);
    }

    [Fact]
    public void Oversized_messages_are_refused_and_limited_text_fits()
    {
        var text = new string('x', MessageFraming.MaxMessageBytes);
        var error = Assert.Throws<ProtocolException>(() => MessageFraming.Encode(new Log { SessionId = "s", Text = text }));
        Assert.Equal(ProtocolException.InvalidLength, error.Code);
        var truncated = false;
        var log = new Log { SessionId = "s", Text = MessageText.Limit(text, ref truncated), Truncated = truncated };
        Assert.True(log.Truncated);
        Assert.InRange(MessageFraming.Encode(log).Length, 1, MessageFraming.MaxMessageBytes);
        var unchanged = false;
        Assert.Equal("short", MessageText.Limit("short", ref unchanged));
        Assert.False(unchanged);
    }

    [Fact]
    public void Limited_text_does_not_split_a_surrogate_pair()
    {
        var text = new string('x', MessageText.MaxChars - 1) + "\U0001F600" + "tail";
        var truncated = false;
        var limited = MessageText.Limit(text, ref truncated);
        Assert.True(truncated);
        Assert.Equal(MessageText.MaxChars - 1, limited.Length);
        Assert.False(char.IsHighSurrogate(limited[limited.Length - 1]));
    }

    [Fact]
    public void Only_protocol_types_can_be_encoded()
    {
        Assert.Throws<ArgumentException>(() => MessageFraming.Encode(new Impostor.Hello()));
    }

    internal static byte[] Frame(byte[] payload) => BitConverter.GetBytes(payload.Length).Concat(payload).ToArray();

    private static class Impostor
    {
        // Same simple name as a protocol type, but not one.
        public sealed class Hello : Message { }
    }

    private sealed class TrickleStream : MemoryStream
    {
        public int Reads;
        public TrickleStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reads++;
            return base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
        }
    }
}

public class SessionTrackerTests
{
    [Fact]
    public void Complete_consistent_session_has_no_problems()
    {
        var tracker = Track(Session(), all: true);
        Assert.Empty(tracker.Problems());
        Assert.Equal(new[] { "fact", "row1", "row2" }, tracker.Results.Select(r => r.TestId));
        Assert.Equal(42, tracker.LastHeartbeat.FrameCount);
        Assert.Equal(1234, tracker.Host.ProcessId);
    }

    [Fact]
    public void Missing_final_event_cannot_succeed()
    {
        var events = Session();
        var tracker = Track(events.Take(events.Count - 1));
        Assert.Contains(tracker.Problems(), p => p.Contains("did not send SessionFinished"));
    }

    [Fact]
    public void Disconnect_during_a_test_reports_the_unfinished_test()
    {
        var tracker = Track(Session().Take(2));
        var problems = tracker.Problems();
        Assert.Contains(problems, p => p.Contains("did not send SessionFinished"));
        Assert.Contains(problems, p => p.Contains("did not finish: fact"));
    }

    [Theory]
    [InlineData(1, 1)] // duplicate
    [InlineData(1, 3)] // gap
    [InlineData(2, 1)] // reordered
    public void Duplicate_missing_or_reordered_sequences_are_rejected(int index, long sequence)
    {
        var events = Session();
        events[index].Sequence = sequence;
        var error = Assert.Throws<ProtocolException>(() => Track(events.Take(index + 1)));
        Assert.Equal(ProtocolException.OutOfOrder, error.Code);
    }

    [Fact]
    public void Events_after_the_final_event_are_rejected()
    {
        var events = Session();
        events.Add(new Log { SessionId = "s", Sequence = events.Count + 1, Text = "late" });
        Assert.Throws<ProtocolException>(() => Track(events));
    }

    [Fact]
    public void Repeated_or_unstarted_results_are_rejected()
    {
        var tracker = new SessionTracker("s");
        tracker.Accept(new Welcome { SessionId = "s", Sequence = 1 });
        tracker.Accept(new TestStarted { SessionId = "s", Sequence = 2, TestId = "t" });
        tracker.Accept(Finished(3, "t", TestFinished.Passed));
        Assert.Throws<ProtocolException>(() => tracker.Accept(Finished(4, "t", TestFinished.Passed)));
        var overlap = new SessionTracker("s");
        overlap.Accept(new Welcome { SessionId = "s", Sequence = 1 });
        overlap.Accept(new TestStarted { SessionId = "s", Sequence = 2, TestId = "t" });
        Assert.Throws<ProtocolException>(() => overlap.Accept(new TestStarted { SessionId = "s", Sequence = 3, TestId = "t" }));
    }

    [Fact]
    public void A_case_reports_once_per_row_after_each_finishes()
    {
        var tracker = new SessionTracker("s");
        tracker.Accept(new Welcome { SessionId = "s", Sequence = 1 });
        tracker.Accept(new TestStarted { SessionId = "s", Sequence = 2, TestId = "theory", DisplayName = "Theory(1)" });
        tracker.Accept(Finished(3, "theory", TestFinished.Passed));
        tracker.Accept(new TestStarted { SessionId = "s", Sequence = 4, TestId = "theory", DisplayName = "Theory(2)" });
        tracker.Accept(Finished(5, "theory", TestFinished.Passed));
        tracker.Accept(new SessionFinished { SessionId = "s", Sequence = 6, Passed = 2, Cleanup = SessionFinished.Clean });
        Assert.Empty(tracker.Problems());
        Assert.Equal(2, tracker.Results.Count);
    }

    [Fact]
    public void Other_sessions_requests_unknown_outcomes_and_late_welcome_are_rejected()
    {
        Assert.Throws<ProtocolException>(() => new SessionTracker("s").Accept(new Welcome { SessionId = "other", Sequence = 1 }));
        Assert.Throws<ProtocolException>(() => new SessionTracker("s").Accept(new Hello { SessionId = "s" }));
        Assert.Throws<ProtocolException>(() => new SessionTracker("s").Accept(new Heartbeat { SessionId = "s", Sequence = 1 }));
        var tracker = new SessionTracker("s");
        tracker.Accept(new Welcome { SessionId = "s", Sequence = 1 });
        tracker.Accept(new TestStarted { SessionId = "s", Sequence = 2, TestId = "t" });
        Assert.Throws<ProtocolException>(() => tracker.Accept(Finished(3, "t", "inconclusive")));
    }

    [Fact]
    public void Count_mismatch_dirty_cleanup_and_host_error_are_problems()
    {
        var events = Session();
        var final = (SessionFinished)events[events.Count - 1];
        final.Passed = 2;
        final.Cleanup = SessionFinished.Dirty;
        final.Error = "player not loaded";
        ((TestFinished)events[2]).Cleanup = SessionFinished.Dirty;
        var problems = Track(events).Problems();
        Assert.Contains(problems, p => p.Contains("SessionFinished reports 2 passed"));
        Assert.Contains(problems, p => p.Contains("Session cleanup is dirty"));
        Assert.Contains(problems, p => p.Contains("player not loaded"));
        Assert.Contains(problems, p => p.Contains("Cleanup after fact is dirty"));
    }

    [Fact]
    public void Failed_and_skipped_tests_are_results_not_protocol_problems()
    {
        var events = Session();
        ((TestFinished)events[2]).Outcome = TestFinished.Failed;
        ((TestFinished)events[4]).Outcome = TestFinished.Skipped;
        var final = (SessionFinished)events[events.Count - 1];
        final.Passed = 1;
        final.Failed = 1;
        final.Skipped = 1;
        var tracker = Track(events);
        Assert.Empty(tracker.Problems());
        Assert.Equal(new[] { TestFinished.Failed, TestFinished.Skipped, TestFinished.Passed }, tracker.Results.Select(r => r.Outcome));
    }

    // Welcome, one Fact and two Theory rows, a heartbeat, then the final event.
    internal static List<Event> Session()
    {
        var events = new List<Event> {
            new Welcome { ProcessId = 1234, MainThreadId = 1 },
            new TestStarted { TestId = "fact" }, Finished(0, "fact", TestFinished.Passed),
            new TestStarted { TestId = "row1" }, Finished(0, "row1", TestFinished.Passed),
            new Heartbeat { FrameCount = 42 },
            new TestStarted { TestId = "row2" }, Finished(0, "row2", TestFinished.Passed),
            new SessionFinished { Discovered = 3, Passed = 3, Cleanup = SessionFinished.Clean }
        };
        for (int index = 0; index < events.Count; index++) { events[index].SessionId = "s"; events[index].Sequence = index + 1; }
        return events;
    }

    internal static SessionTracker Track(IEnumerable<Event> events, bool all = false)
    {
        var tracker = new SessionTracker("s");
        foreach (var item in events) tracker.Accept(item);
        if (all) Assert.NotNull(tracker.Final);
        return tracker;
    }

    private static TestFinished Finished(long sequence, string id, string outcome)
        => new TestFinished { SessionId = "s", Sequence = sequence, TestId = id, Outcome = outcome, Cleanup = SessionFinished.Clean };
}

public class EventWriterTests
{
    [Fact]
    public async Task Concurrent_writers_produce_one_ordered_stream_over_a_socket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var connecting = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await connecting.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await accept;
        listener.Stop();
        var server = accepted.GetStream();
        var client = connecting.GetStream();
        var writer = new EventWriter(server, "s");
        var tracker = new SessionTracker("s");
        var reader = Task.Run(async () => {
            Message message;
            while ((message = await MessageFraming.ReadAsync(client)) != null) tracker.Accept(message);
        });
        await writer.WriteAsync(new Welcome());
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() => writer.WriteAsync(new Log { Text = "line " + index }))));
        await writer.WriteAsync(new SessionFinished { Cleanup = SessionFinished.Clean });
        accepted.Close();
        Assert.Same(reader, await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(30))));
        await reader;
        Assert.Empty(tracker.Problems());
        Assert.Equal(202, tracker.Final.Sequence);
    }

    [Fact]
    public async Task A_failed_write_stops_the_session_instead_of_skipping_a_number()
    {
        var stream = new FailingStream();
        var writer = new EventWriter(stream, "s");
        await writer.WriteAsync(new Welcome());
        stream.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(new Log { Text = "lost" }));
        stream.Fail = false;
        var error = await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new Log { Text = "after" }));
        Assert.Equal(ProtocolException.Disconnected, error.Code);
    }

    [Fact]
    public async Task An_oversized_event_is_refused_without_consuming_a_number()
    {
        var stream = new MemoryStream();
        var writer = new EventWriter(stream, "s");
        await writer.WriteAsync(new Welcome());
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new Log { Text = new string('x', MessageFraming.MaxMessageBytes) }));
        await writer.WriteAsync(new SessionFinished { Cleanup = SessionFinished.Clean });
        stream.Position = 0;
        var tracker = new SessionTracker("s");
        Message message;
        while ((message = await MessageFraming.ReadAsync(stream)) != null) tracker.Accept(message);
        Assert.Empty(tracker.Problems());
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool Fail;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Fail ? Task.FromException(new IOException("pipe broken")) : base.WriteAsync(buffer, offset, count, cancellationToken);
    }
}
