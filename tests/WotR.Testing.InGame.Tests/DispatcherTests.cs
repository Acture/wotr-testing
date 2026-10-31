// Manually driven updates must stay on the test thread: Update rejects any thread other than the first.
#pragma warning disable xUnit1031

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame;
using Xunit;

namespace WotR.Testing.InGame.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task Body_and_awaited_continuations_run_on_the_main_thread()
    {
        using var player = new FakePlayer();
        var threads = new List<int>();
        long before = 0, after = 0;
        await Within(player.Dispatcher.Run(async () => {
            threads.Add(Thread.CurrentThread.ManagedThreadId);
            before = player.Dispatcher.FrameCount;
            await player.Dispatcher.NextFrame();
            after = player.Dispatcher.FrameCount;
            threads.Add(Thread.CurrentThread.ManagedThreadId);
            await Task.Delay(10);
            threads.Add(Thread.CurrentThread.ManagedThreadId);
            await Task.Run(() => Thread.Sleep(1));
            threads.Add(Thread.CurrentThread.ManagedThreadId);
        }));
        Assert.Equal(4, threads.Count);
        Assert.All(threads, thread => Assert.Equal(player.ThreadId, thread));
        Assert.Equal(player.ThreadId, player.Dispatcher.MainThreadId);
        Assert.True(after > before);
    }

    [Fact]
    public void Next_frame_needs_a_greater_frame_count_not_another_update()
    {
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1));
        var resumed = new List<long>();
        var run = dispatcher.Run(async () => {
            await dispatcher.NextFrame();
            resumed.Add(dispatcher.FrameCount);
            await dispatcher.NextFrame();
            resumed.Add(dispatcher.FrameCount);
        });
        dispatcher.Update(10);
        dispatcher.Update(10);
        Assert.Empty(resumed);
        dispatcher.Update(11);
        Assert.Equal(new[] { 11L }, resumed);
        dispatcher.Update(12);
        Assert.Equal(new[] { 11L, 12L }, resumed);
        Assert.True(run.Wait(5000));
    }

    [Fact]
    public void Wait_until_checks_once_per_update_against_a_wall_clock_deadline()
    {
        var now = TimeSpan.Zero;
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1), () => now);
        int checks = 0;
        bool ready = false;
        var waited = dispatcher.Run(() => dispatcher.WaitUntil(() => { checks++; return ready; }, TimeSpan.FromSeconds(5)));
        dispatcher.Update(1);
        dispatcher.Update(2);
        Assert.False(waited.IsCompleted);
        ready = true;
        dispatcher.Update(3);
        Assert.True(waited.Wait(5000));
        Assert.Equal(3, checks);

        var expired = dispatcher.Run(() => dispatcher.WaitUntil(() => false, TimeSpan.FromSeconds(5)));
        // Many frames without wall time passing do not reach the deadline.
        for (int frame = 4; frame < 100; frame++) dispatcher.Update(frame);
        Assert.False(expired.IsCompleted);
        now = TimeSpan.FromSeconds(5);
        dispatcher.Update(100);
        Assert.IsType<TimeoutException>(Assert.Throws<AggregateException>(() => expired.Wait(5000)).InnerException);
    }

    [Fact]
    public void Wait_until_propagates_predicate_exceptions()
    {
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1));
        int calls = 0;
        var waited = dispatcher.Run(() => dispatcher.WaitUntil(() => ++calls > 1 ? throw new InvalidOperationException("broken") : false, TimeSpan.FromMinutes(1)));
        dispatcher.Update(1);
        dispatcher.Update(2);
        Assert.Equal("broken", Assert.Throws<AggregateException>(() => waited.Wait(5000)).InnerException.Message);
    }

    [Fact]
    public void Budget_defers_remaining_and_newly_queued_work_to_later_updates()
    {
        var now = TimeSpan.Zero;
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromMilliseconds(5), () => now);
        var ran = new List<int>();
        for (int index = 0; index < 4; index++) {
            var item = index;
            dispatcher.Run(() => { now += TimeSpan.FromMilliseconds(3); ran.Add(item); return Task.CompletedTask; });
        }
        dispatcher.Update(1);
        Assert.Equal(new[] { 0, 1 }, ran);
        bool inner = false;
        dispatcher.Run(() => { dispatcher.Run(() => { inner = true; return Task.CompletedTask; }); return Task.CompletedTask; });
        dispatcher.Update(2);
        Assert.Equal(new[] { 0, 1, 2, 3 }, ran);
        Assert.False(inner);
        dispatcher.Update(3);
        dispatcher.Update(4);
        Assert.True(inner);
    }

    [Fact]
    public void Exceptions_and_cancellation_reach_the_caller()
    {
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1));
        var thrown = dispatcher.Run(() => throw new ApplicationException("sync"));
        var later = dispatcher.Run(async () => { await dispatcher.NextFrame(); throw new ApplicationException("async"); });
        bool invoked = false;
        var canceled = dispatcher.Run(() => { invoked = true; return Task.CompletedTask; }, new CancellationToken(true));
        dispatcher.Update(1);
        dispatcher.Update(2);
        Assert.Equal("sync", Assert.Throws<AggregateException>(() => thrown.Wait(5000)).InnerException.Message);
        Assert.Equal("async", Assert.Throws<AggregateException>(() => later.Wait(5000)).InnerException.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Throws<AggregateException>(() => canceled.Wait(5000)).InnerException);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Frame_operations_reject_code_that_left_the_main_thread()
    {
        using var player = new FakePlayer();
        Assert.Throws<InvalidOperationException>(() => RequestNextFrame(new MainThreadDispatcher(TimeSpan.Zero)));
        await Within(player.Dispatcher.Run(() => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => RequestNextFrame(player.Dispatcher));
        Assert.Throws<InvalidOperationException>(() => player.Dispatcher.Update(long.MaxValue));
        var escaped = player.Dispatcher.Run(async () => {
            await Task.Delay(1).ConfigureAwait(false);
            await player.Dispatcher.NextFrame();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Within(escaped));
    }

    [Fact]
    public void Stop_fails_unfinished_work_and_rejects_new_work()
    {
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1));
        var waiting = dispatcher.Run(async () => await dispatcher.NextFrame());
        var queued = dispatcher.Run(() => Task.CompletedTask);
        dispatcher.Update(1);
        Assert.True(queued.Wait(5000));
        var neverStarted = dispatcher.Run(() => Task.CompletedTask);
        dispatcher.Stop();
        dispatcher.Update(2);
        Assert.IsType<ObjectDisposedException>(Assert.Throws<AggregateException>(() => waiting.Wait(5000)).InnerException);
        Assert.IsType<ObjectDisposedException>(Assert.Throws<AggregateException>(() => neverStarted.Wait(5000)).InnerException);
        Assert.IsType<ObjectDisposedException>(Assert.Throws<AggregateException>(() => dispatcher.Run(() => Task.CompletedTask).Wait(5000)).InnerException);
    }

    [Fact]
    public void Async_void_failures_are_reported_instead_of_breaking_the_update()
    {
        var dispatcher = new MainThreadDispatcher(TimeSpan.FromSeconds(1));
        var reported = new List<Exception>();
        dispatcher.UnhandledException += reported.Add;
        async void Escape() { await dispatcher.NextFrame(); throw new ApplicationException("void"); }
        dispatcher.Run(() => { Escape(); return Task.CompletedTask; });
        var after = dispatcher.Run(async () => { await dispatcher.NextFrame(); await dispatcher.NextFrame(); });
        for (int frame = 1; frame <= 4; frame++) dispatcher.Update(frame);
        Assert.Equal("void", Assert.Single(reported).Message);
        Assert.True(after.Wait(5000));
    }

    // The check is synchronous; the returned task is never awaited.
    private static void RequestNextFrame(MainThreadDispatcher dispatcher) => dispatcher.NextFrame();

    private static async Task Within(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))));
        await task;
    }

    // Stands in for Unity: one thread calls Update with an increasing frame count.
    private sealed class FakePlayer : IDisposable
    {
        public readonly MainThreadDispatcher Dispatcher = new MainThreadDispatcher(TimeSpan.FromMilliseconds(5));
        private readonly Thread thread;
        private readonly ManualResetEventSlim started = new ManualResetEventSlim();
        private volatile bool stop;
        public int ThreadId;

        public FakePlayer()
        {
            thread = new Thread(() => {
                ThreadId = Thread.CurrentThread.ManagedThreadId;
                for (long frame = 1; !stop; frame++) {
                    Dispatcher.Update(frame);
                    started.Set();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            thread.Start();
            started.Wait();
        }

        public void Dispose()
        {
            stop = true;
            thread.Join();
        }
    }
}
