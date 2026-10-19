using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace WotR.Testing.InGame
{
    /// <summary>
    /// Runs test bodies on Unity's main thread in the running game. Use it as an xUnit class or collection fixture;
    /// constructing it does no Unity work.
    /// </summary>
    public sealed class InGameFixture
    {
        public Task OnMainThread(Func<InGameContext, Task> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            return OnMainThread<bool>(async context => { await body(context); return true; });
        }

        /// <summary>
        /// Queues <paramref name="body"/> for the main thread and completes when it and its registered cleanups finish.
        /// Cleanups run in reverse order even when the body fails. A failed cleanup fails the test and marks the session
        /// dirty, because the game may keep state that later tests do not expect.
        /// </summary>
        public Task<T> OnMainThread<T>(Func<InGameContext, Task<T>> body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            var dispatcher = InGameRuntime.Require();
            var result = default(T);
            var run = dispatcher.Run(async () => {
                var context = new InGameContext(dispatcher, InGameRuntime.SessionCancellation);
                ExceptionDispatchInfo failure = null;
                try { result = await body(context); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                var cleanup = await context.CleanupAsync();
                if (cleanup.Count > 0) {
                    InGameRuntime.MarkDirty("cleanup failed: " + string.Join("; ", cleanup.Select(error => error.Message)));
                    var all = new List<Exception>();
                    if (failure != null) all.Add(failure.SourceException);
                    all.AddRange(cleanup);
                    throw new InGameCleanupException(all);
                }
                failure?.Throw();
            }, InGameRuntime.SessionCancellation);
            return run.ContinueWith(done => {
                done.GetAwaiter().GetResult();
                return result;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Owned-resource cleanup failed; the game may now hold state from this test.</summary>
    public sealed class InGameCleanupException : AggregateException
    {
        public InGameCleanupException(IEnumerable<Exception> errors)
            : base("[CLEANUP_FAILED] In-game cleanup failed; the session is dirty and needs a restart before the next run.", errors) { }
    }
}
