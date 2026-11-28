using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WotR.Testing.InGame
{
    /// <summary>
    /// Runs test work on Unity's main thread from the game's update callback, without ever blocking that callback.
    /// Independent of Unity: the host passes the frame count to <see cref="Update"/>.
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        private readonly ConcurrentDictionary<TaskCompletionSource<bool>, byte> running = new ConcurrentDictionary<TaskCompletionSource<bool>, byte>();
        // Main thread only.
        private readonly List<KeyValuePair<long, TaskCompletionSource<bool>>> frames = new List<KeyValuePair<long, TaskCompletionSource<bool>>>();
        private readonly List<Condition> conditions = new List<Condition>();
        private readonly TimeSpan budget;
        private readonly Func<TimeSpan> clock;
        private readonly Context context;
        private int mainThread;
        private long frame = long.MinValue;
        private volatile bool stopped;

        /// <param name="budget">Time per update for starting queued work; a running callback cannot be interrupted.</param>
        /// <param name="clock">Wall-clock source for the budget and deadlines; defaults to a stopwatch.</param>
        public MainThreadDispatcher(TimeSpan budget, Func<TimeSpan> clock = null)
        {
            this.budget = budget;
            if (clock == null) {
                var watch = Stopwatch.StartNew();
                clock = () => watch.Elapsed;
            }
            this.clock = clock;
            context = new Context(this);
        }

        /// <summary>Work that escaped a test's task, such as an exception thrown by an <c>async void</c> callback.</summary>
        public event Action<Exception> UnhandledException;

        /// <summary>The thread of the first <see cref="Update"/>; 0 before it.</summary>
        public int MainThreadId => Volatile.Read(ref mainThread);
        public long FrameCount => Interlocked.Read(ref frame);
        public bool IsMainThread => MainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == MainThreadId;

        /// <summary>Called by the host once per Unity frame, on the main thread.</summary>
        public void Update(long frameCount)
        {
            if (MainThreadId == 0) Volatile.Write(ref mainThread, Thread.CurrentThread.ManagedThreadId);
            else if (!IsMainThread) throw new InvalidOperationException("Update must be called on the thread of the first update.");
            if (stopped) { frames.Clear(); conditions.Clear(); return; }
            var advanced = frameCount > FrameCount;
            if (advanced) Interlocked.Exchange(ref frame, frameCount);
            var previous = SynchronizationContext.Current;
            // Continuations of awaits in test code capture this context and return through the queue.
            SynchronizationContext.SetSynchronizationContext(context);
            try {
                // Waiters resume through the queue before the snapshot below, so they run in this update.
                if (advanced) {
                    var due = frames.FindAll(waiter => waiter.Key < frameCount);
                    frames.RemoveAll(waiter => waiter.Key < frameCount);
                    foreach (var waiter in due) waiter.Value.TrySetResult(true);
                }
                foreach (var condition in conditions.ToArray())
                    if (condition.Check(clock())) conditions.Remove(condition);
                var start = clock();
                // Work queued during this update waits for the next one, so a loop of posts cannot hold the frame.
                for (int count = queue.Count; count > 0 && !stopped && queue.TryDequeue(out var work); count--) {
                    try { work(); }
                    catch (Exception error) { UnhandledException?.Invoke(error); }
                    if (clock() - start >= budget) break;
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        /// <summary>Queues <paramref name="body"/> for the main thread; the task completes when the body's task does.</summary>
        public Task Run(Func<Task> body, CancellationToken cancellationToken = default)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            running.TryAdd(result, 0);
            result.Task.ContinueWith(_ => running.TryRemove(result, out var __), TaskScheduler.Default);
            if (stopped) result.TrySetException(Stopped());
            else Post(() => {
                if (cancellationToken.IsCancellationRequested) { result.TrySetCanceled(cancellationToken); return; }
                Task task;
                try { task = body() ?? throw new InvalidOperationException("The main-thread body returned no task."); }
                catch (Exception error) { result.TrySetException(error); return; }
                task.ContinueWith(done => {
                    if (done.IsFaulted) result.TrySetException(done.Exception.InnerExceptions);
                    else if (done.IsCanceled) result.TrySetCanceled();
                    else result.TrySetResult(true);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            });
            return result.Task;
        }

        /// <summary>Completes in a later update with a strictly greater frame count.</summary>
        public Task NextFrame()
        {
            RequireMainThread(nameof(NextFrame));
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            frames.Add(new KeyValuePair<long, TaskCompletionSource<bool>>(FrameCount, waiter));
            return waiter.Task;
        }

        /// <summary>Checks <paramref name="condition"/> now and then once per update on the main thread, until a wall-clock deadline.</summary>
        public Task WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (condition == null) throw new ArgumentNullException(nameof(condition));
            RequireMainThread(nameof(WaitUntil));
            var waiter = new Condition(condition, clock() + timeout, cancellationToken);
            if (!waiter.Check(clock())) conditions.Add(waiter);
            return waiter.Task;
        }

        public void RequireMainThread(string operation)
        {
            if (!IsMainThread)
                throw new InvalidOperationException(operation + " must run on Unity's main thread. Code after ConfigureAwait(false) or inside Task.Run is not on it.");
        }

        /// <summary>Discards queued work and fails every unfinished <see cref="Run"/>; later calls fail immediately.</summary>
        public void Stop()
        {
            stopped = true;
            while (queue.TryDequeue(out var _)) { }
            foreach (var result in running.Keys) result.TrySetException(Stopped());
        }

        private void Post(Action work)
        {
            if (!stopped) queue.Enqueue(work);
        }

        private static ObjectDisposedException Stopped() => new ObjectDisposedException(nameof(MainThreadDispatcher), "The in-game host stopped before the main-thread work finished.");

        private sealed class Condition
        {
            private readonly Func<bool> predicate;
            private readonly TimeSpan deadline;
            private readonly CancellationToken cancellationToken;
            private readonly TaskCompletionSource<bool> result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Condition(Func<bool> predicate, TimeSpan deadline, CancellationToken cancellationToken)
            {
                this.predicate = predicate;
                this.deadline = deadline;
                this.cancellationToken = cancellationToken;
            }

            internal Task Task => result.Task;

            /// <summary>True when the wait has ended in any way.</summary>
            internal bool Check(TimeSpan now)
            {
                if (cancellationToken.IsCancellationRequested) result.TrySetCanceled(cancellationToken);
                else {
                    try {
                        if (predicate()) result.TrySetResult(true);
                        else if (now < deadline) return false;
                        else result.TrySetException(new TimeoutException("The condition was not met before its deadline."));
                    }
                    catch (Exception error) { result.TrySetException(error); }
                }
                return true;
            }
        }

        private sealed class Context : SynchronizationContext
        {
            private readonly MainThreadDispatcher owner;
            internal Context(MainThreadDispatcher owner) => this.owner = owner;
            public override void Post(SendOrPostCallback callback, object state) => owner.Post(() => callback(state));
            public override void Send(SendOrPostCallback callback, object state)
            {
                // Blocking a worker on the main thread invites deadlock while that thread waits for the worker.
                if (!owner.IsMainThread) throw new NotSupportedException("Blocking calls into Unity's main thread are not supported; await Run instead.");
                callback(state);
            }
            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
