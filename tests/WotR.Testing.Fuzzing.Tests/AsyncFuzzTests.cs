using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FsCheck.Fluent;
using WotR.Testing.Fuzzing;
using Xunit;

namespace WotR.Testing.Fuzzing.Tests;

public class AsyncFuzzTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Async_stateful_defect_is_found_shrunk_and_replayed_with_a_fresh_target_each_time()
    {
        var made = new List<AsyncLedger>();
        var options = new FuzzOptions { Cases = 100, MaxCalls = 10, Seed = 31, MaxShrinks = 300 };
        var report = await Fuzz.RunAsync<IAsyncLedger>(async () => {
            await Task.Yield();
            var ledger = new AsyncLedger();
            made.Add(ledger);
            return ledger;
        }, options);
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Contains(".Withdraw(", report.Failure.Method);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.Failure.ExceptionType);
        Assert.True(report.ShrinkAttempts > 0);
        // Every case and executed shrink attempt constructed its own target and disposed it once. A repeated candidate
        // still counts as an attempt (bounding cycling shrinkers) but is not executed again.
        Assert.InRange(made.Count, report.CasesAttempted + 1, report.CasesAttempted + report.ShrinkAttempts);
        Assert.Equal(made.Count, made.Distinct().Count());
        Assert.All(made, ledger => Assert.Equal(1, ledger.DisposeCount));

        using var saved = JsonDocument.Parse(report.ReplayCase);
        var expected = saved.RootElement.GetProperty("Calls").EnumerateArray().Select(call =>
            call.GetProperty("Method").GetString().Contains(".Deposit(") ? "Deposit:" + call.GetProperty("Arguments")[0].GetString() : "Withdraw").ToList();
        Assert.Equal(new[] { "Deposit", "Deposit", "Withdraw" }, expected.Select(call => call.Split(':')[0]));
        Assert.DoesNotContain("Deposit:0", expected);

        var log = new List<string>();
        AsyncLedger replayed = null;
        var replay = await Fuzz.ReplayAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(replayed = new AsyncLedger(log)), report.ReplayCase, options);
        Assert.Equal(FuzzStatus.Finding, replay.Status);
        Assert.Equal(report.ReplayCase, replay.ReplayCase);
        Assert.Equal(1, replay.CasesAttempted);
        Assert.Equal(0, replay.ShrinkAttempts);
        Assert.Equal(expected, log);
        Assert.Equal(1, replayed.DisposeCount);
        Assert.True((await Fuzz.ReplayAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(new FixedLedger()), report.ReplayCase, options)).Succeeded);
    }

    [Fact]
    public async Task Every_case_shrink_attempt_and_replay_gets_a_fresh_disposed_target()
    {
        // Distinct candidates only: one rejected (value - 1000) and one accepted (value - 1) per step from 30 down to 10.
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1 };
        options.Generators.Register(Arb.From(Gen.Constant(30), value => value > 10 ? new[] { value - 1000, value - 1 } : new int[0]));
        var made = new List<AsyncThreshold>();
        Task<IAsyncThreshold> Create() { var target = new AsyncThreshold(); made.Add(target); return Task.FromResult<IAsyncThreshold>(target); }
        var report = await Fuzz.RunAsync<IAsyncThreshold>(Create, options);
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Equal(1, report.CasesAttempted);
        Assert.Equal(40, report.ShrinkAttempts);
        Assert.Equal(report.CasesAttempted + report.ShrinkAttempts, made.Count);
        Assert.Contains("\"10\"", report.ReplayCase);
        await Fuzz.ReplayAsync<IAsyncThreshold>(Create, report.ReplayCase, options);
        Assert.Equal(report.CasesAttempted + report.ShrinkAttempts + 1, made.Count);
        Assert.Equal(made.Count, made.Distinct().Count());
        Assert.All(made, target => Assert.Equal(1, target.DisposeCount));
    }

    [Fact]
    public async Task Same_seed_gives_the_same_report_through_run_and_run_async()
    {
        var options = new FuzzOptions { Cases = 100, MaxCalls = 10, Seed = 31, MaxShrinks = 300 };
        var syncLog = new List<string>();
        // Off the test's synchronization context, which the synchronous API needs for targets that await.
        var sync = await Task.Run(() => Fuzz.Run<IAsyncLedger>(() => new AsyncLedger(syncLog), options));
        var asyncLog = new List<string>();
        var asynchronous = await Fuzz.RunAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(new AsyncLedger(asyncLog)), options);
        Assert.Equal(FuzzStatus.Finding, sync.Status);
        Assert.Equal(WithoutStackTrace(sync), WithoutStackTrace(asynchronous));
        Assert.Equal(syncLog, asyncLog);

        var passing = new FuzzOptions { Cases = 30, MaxCalls = 6, Seed = 77 };
        var syncPass = await Task.Run(() => Fuzz.Run<IAsyncLedger>(() => new FixedLedger(), passing));
        var asyncPass = await Fuzz.RunAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(new FixedLedger()), passing);
        Assert.True(asyncPass.Succeeded);
        Assert.Equal(syncPass.ToJson(), asyncPass.ToJson());
    }

    [Fact]
    public async Task Check_async_throws_with_the_report_and_replay_async_validates_first()
    {
        var options = new FuzzOptions { Cases = 100, MaxCalls = 10, Seed = 31 };
        var failure = await Assert.ThrowsAsync<FuzzFailureException>(() =>
            Fuzz.CheckAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(new AsyncLedger()), options));
        Assert.Equal(FuzzStatus.Finding, failure.Report.Status);
        Assert.Contains(failure.Report.ReplayCase, failure.Message);
        Assert.True((await Fuzz.CheckAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(new FixedLedger()), options)).Succeeded);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Fuzz.ReplayAsync<IAsyncLedger>(() => throw new Exception("must not run"), "{}"));
    }

    [Fact]
    public async Task Async_factory_failures_are_infrastructure_failures()
    {
        var options = new FuzzOptions { Cases = 1 };
        var nullTask = await Fuzz.RunAsync<IAsyncLedger>(() => null, options);
        Assert.Equal(FuzzStatus.InfrastructureFailure, nullTask.Status);
        Assert.Equal("factory", nullTask.Failure.Kind);
        Assert.Contains("null task", nullTask.Failure.Message);
        var nullTarget = await Fuzz.RunAsync<IAsyncLedger>(() => Task.FromResult<IAsyncLedger>(null), options);
        Assert.Equal("factory", nullTarget.Failure.Kind);
        Assert.Contains("returned null", nullTarget.Failure.Message);
        var faulted = await Fuzz.RunAsync<IAsyncLedger>(async () => { await Task.Yield(); throw new ApplicationException("factory"); }, options);
        Assert.Equal("factory", faulted.Failure.Kind);
        Assert.Equal(typeof(ApplicationException).FullName, faulted.Failure.ExceptionType);
        Assert.Equal(0, faulted.ShrinkAttempts);
    }

    [Fact]
    public async Task Async_run_on_a_frame_pump_keeps_all_target_code_on_the_pump_thread()
    {
        using var pump = new FramePump();
        var probe = new FrameProbe(pump);
        var options = new FuzzOptions { Cases = 20, MaxCalls = 6, Seed = 5, MaxShrinks = 100 };
        var report = await Within(pump.Run(() => Fuzz.RunAsync<IFrameApi>(async () => {
            probe.Record("factory");
            await probe.WaitFrame();
            probe.Record("factory resumed");
            return new FrameApi(probe);
        }, options)));
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Contains(".Commit(", report.Failure.Method);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.Failure.ExceptionType);
        Assert.True(report.ShrinkAttempts > 0);

        var replay = await Within(pump.Run(() =>
            Fuzz.ReplayAsync<IFrameApi>(() => Task.FromResult<IFrameApi>(new FrameApi(probe)), report.ReplayCase, options)));
        Assert.Equal(FuzzStatus.Finding, replay.Status);
        Assert.Equal(report.ReplayCase, replay.ReplayCase);

        var events = probe.Events.ToArray();
        foreach (var name in new[] { "factory", "factory resumed", "Step", "Step resumed", "Commit", "Commit resumed", "Dispose" })
            Assert.Contains(events, item => item.Key == name);
        Assert.All(events, item => Assert.Equal(pump.ThreadId, item.Value));
        Assert.All(probe.FrameWaits, wait => Assert.True(wait.Value > wait.Key, "A frame wait resumed in the frame it started."));
        Assert.Equal(events.Count(item => item.Key == "constructor"), events.Count(item => item.Key == "Dispose"));
        Assert.Empty(pump.Errors);
    }

    [Fact]
    public async Task Synchronous_run_on_the_frame_pump_stops_its_frames_until_released()
    {
        using var pump = new FramePump();
        var probe = new FrameProbe(pump);
        var options = new FuzzOptions { Cases = 1, MaxCalls = 1, MaxShrinks = 0 };
        var run = pump.Run(() => Task.FromResult(Fuzz.Run<IFrameApi>(() => new FrameApi(probe), options)));
        // The target waits for a frame while the synchronous engine blocks the only thread that produces frames.
        var deadline = DateTime.UtcNow + Timeout;
        while (pump.Waiting == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(1, pump.Waiting);
        long frame = pump.Frame;
        await Task.Delay(500);
        Assert.False(run.IsCompleted);
        Assert.Equal(frame, pump.Frame);
        Assert.Equal(1, pump.Waiting);

        // Release it without leaving a hung thread: fail the frame wait and let its continuation run elsewhere.
        pump.Abandon();
        var report = await Within(run);
        Assert.Equal(FuzzStatus.Finding, report.Status);
        Assert.Equal(typeof(PumpAbandonedException).FullName, report.Failure.ExceptionType);
        Assert.DoesNotContain(probe.Events, item => item.Key.EndsWith(" resumed"));
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(Timeout)) == task, "The run did not finish within " + Timeout + "; it is probably deadlocked.");
        return await task;
    }

    private static string WithoutStackTrace(FuzzReport report)
    {
        // Stack traces differ between the blocking and awaiting paths; every other field must match.
        var json = JsonNode.Parse(report.ToJson()).AsObject();
        (json["Failure"] as JsonObject)?.Remove("ExceptionDetails");
        return json.ToJsonString();
    }

    public interface IAsyncLedger { Task Deposit(int amount); Task<int> Withdraw(); }
    public class AsyncLedger : IAsyncLedger, IDisposable {
        private readonly List<string> log;
        private int deposits;
        public int DisposeCount { get; private set; }
        public AsyncLedger(List<string> log = null) { this.log = log; }
        public async Task Deposit(int amount) { await Task.Yield(); log?.Add("Deposit:" + amount); if (amount != 0) deposits++; }
        public virtual async Task<int> Withdraw() {
            log?.Add("Withdraw");
            await Task.Delay(1);
            if (deposits >= 2) throw new InvalidOperationException("two deposits");
            return deposits;
        }
        public void Dispose() => DisposeCount++;
    }
    public sealed class FixedLedger : AsyncLedger {
        public override async Task<int> Withdraw() { await Task.Yield(); return 0; }
    }

    public interface IAsyncThreshold { Task Act(int value); }
    public sealed class AsyncThreshold : IAsyncThreshold, IDisposable {
        public int DisposeCount { get; private set; }
        public async Task Act(int value) { await Task.Yield(); if (value >= 10) throw new InvalidOperationException("threshold"); }
        public void Dispose() => DisposeCount++;
    }

    public interface IFrameApi { Task Step(int value); Task Commit(); }
    public sealed class FrameApi : IFrameApi, IDisposable {
        private readonly FrameProbe probe;
        private int steps;
        public FrameApi(FrameProbe probe) { this.probe = probe; probe.Record("constructor"); }
        public async Task Step(int value) {
            probe.Record("Step");
            await probe.WaitFrame();
            probe.Record("Step resumed");
            if (value != 0) steps++;
        }
        public async Task Commit() {
            probe.Record("Commit");
            await probe.WaitFrame();
            probe.Record("Commit resumed");
            if (steps >= 2) throw new InvalidOperationException("two steps across frames");
        }
        public void Dispose() => probe.Record("Dispose");
    }

    /// <summary>Records the thread of each event and the frames around each frame wait.</summary>
    public sealed class FrameProbe {
        private readonly FramePump pump;
        public FrameProbe(FramePump pump) { this.pump = pump; }
        public ConcurrentQueue<KeyValuePair<string, int>> Events { get; } = new();
        public ConcurrentQueue<KeyValuePair<long, long>> FrameWaits { get; } = new();
        public void Record(string name) => Events.Enqueue(new KeyValuePair<string, int>(name, Environment.CurrentManagedThreadId));
        public async Task WaitFrame() {
            long before = pump.Frame;
            await pump.NextFrame();
            FrameWaits.Enqueue(new KeyValuePair<long, long>(before, pump.Frame));
        }
    }
}
