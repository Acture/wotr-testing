using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WotR.Testing.Fuzzing.Tests;

/// <summary>
/// Simulates Unity's main thread: one dedicated thread runs posted callbacks in batches and advances the frame counter
/// between batches. Work posted during a batch runs in the next one.
/// </summary>
public sealed class FramePump : SynchronizationContext, IDisposable
{
    private readonly object gate = new();
    private readonly Queue<KeyValuePair<SendOrPostCallback, object>> queue = new();
    private readonly List<KeyValuePair<long, TaskCompletionSource<bool>>> waiters = new();
    private readonly Thread thread;
    private long frame;
    private bool stopping;
    private bool abandoned;

    public FramePump()
    {
        // Background, so a thread stuck by a failed test cannot keep the test host alive.
        thread = new Thread(Loop) { IsBackground = true, Name = "Frame pump" };
        thread.Start();
    }

    public int ThreadId => thread.ManagedThreadId;
    public long Frame => Interlocked.Read(ref frame);
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public int Waiting
    {
        get { lock (gate) return waiters.Count; }
    }

    /// <summary>Starts <paramref name="body"/> on the pump thread; the task completes with the body's task.</summary>
    public Task<T> Run<T>(Func<Task<T>> body)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => {
            Task<T> task;
            try { task = body(); }
            catch (Exception error) { result.TrySetException(error); return; }
            task.ContinueWith(done => {
                if (done.IsFaulted) result.TrySetException(done.Exception.InnerExceptions);
                else if (done.IsCanceled) result.TrySetCanceled();
                else result.TrySetResult(done.Result);
            }, TaskScheduler.Default);
        }, null);
        return result.Task;
    }

    /// <summary>Completes on a later frame; only the pump thread advances frames.</summary>
    public Task NextFrame()
    {
        if (Thread.CurrentThread != thread) throw new InvalidOperationException("NextFrame must run on the pump thread.");
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (abandoned) waiter.SetException(new PumpAbandonedException());
            else waiters.Add(new KeyValuePair<long, TaskCompletionSource<bool>>(Frame, waiter));
        }
        return waiter.Task;
    }

    public override void Post(SendOrPostCallback callback, object state)
    {
        lock (gate)
        {
            if (!abandoned)
            {
                queue.Enqueue(new KeyValuePair<SendOrPostCallback, object>(callback, state));
                Monitor.PulseAll(gate);
                return;
            }
        }
        // The pump thread may be stuck; finish its remaining continuations elsewhere so a test can end.
        ThreadPool.QueueUserWorkItem(_ => callback(state));
    }

    public override void Send(SendOrPostCallback callback, object state)
    {
        if (Thread.CurrentThread != thread) throw new NotSupportedException("Blocking calls into the pump thread are not supported.");
        callback(state);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Fails every frame wait and stops after the current batch, releasing a pump thread blocked inside a callback.</summary>
    public void Abandon()
    {
        List<TaskCompletionSource<bool>> pending;
        lock (gate)
        {
            abandoned = stopping = true;
            pending = waiters.Select(waiter => waiter.Value).ToList();
            waiters.Clear();
            Monitor.PulseAll(gate);
        }
        foreach (var waiter in pending) waiter.TrySetException(new PumpAbandonedException());
    }

    public void Dispose()
    {
        Abandon();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Loop()
    {
        while (true)
        {
            KeyValuePair<SendOrPostCallback, object>[] batch;
            lock (gate)
            {
                // Idle until there is work or a frame wait; a waiting target needs frames to keep coming.
                while (!stopping && queue.Count == 0 && waiters.Count == 0) Monitor.Wait(gate);
                if (stopping) return;
                batch = queue.ToArray();
                queue.Clear();
            }
            foreach (var item in batch)
            {
                SetSynchronizationContext(this);
                try { item.Key(item.Value); }
                catch (Exception error) { Errors.Enqueue(error); }
            }
            List<TaskCompletionSource<bool>> due;
            lock (gate)
            {
                long current = Interlocked.Increment(ref frame);
                due = waiters.Where(waiter => waiter.Key < current).Select(waiter => waiter.Value).ToList();
                waiters.RemoveAll(waiter => waiter.Key < current);
            }
            // Their continuations come back through Post and run in the next batch.
            foreach (var waiter in due) waiter.TrySetResult(true);
        }
    }
}

public sealed class PumpAbandonedException : Exception
{
    public PumpAbandonedException() : base("The frame pump was abandoned before the frame came.") { }
}
