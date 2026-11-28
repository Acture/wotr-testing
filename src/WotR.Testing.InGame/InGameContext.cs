using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WotR.Testing.InGame
{
    /// <summary>One test body's view of the game: frame waits and resources that are cleaned up after it.</summary>
    public sealed partial class InGameContext
    {
        private readonly MainThreadDispatcher dispatcher;
        private readonly List<Func<Task>> cleanups = new List<Func<Task>>();
        private bool cleaned;

        internal InGameContext(MainThreadDispatcher dispatcher, CancellationToken cancellationToken)
        {
            this.dispatcher = dispatcher;
            CancellationToken = cancellationToken;
        }

        public CancellationToken CancellationToken { get; }
        public long FrameCount => dispatcher.FrameCount;
        public int MainThreadId => dispatcher.MainThreadId;

        /// <summary>Completes in a later frame. Call it on the main thread, as the test body does by default.</summary>
        public Task NextFrame() => dispatcher.NextFrame();

        /// <summary>Checks <paramref name="condition"/> once per frame until it holds; the timeout is wall-clock time.</summary>
        public Task WaitUntil(Func<bool> condition, TimeSpan timeout) => dispatcher.WaitUntil(condition, timeout, CancellationToken);

        /// <summary>Throws unless the caller runs on Unity's main thread.</summary>
        public void AssertMainThread() => dispatcher.RequireMainThread("Game access");

        /// <summary>Destroys <paramref name="value"/> after the test and checks one frame later that it is gone.</summary>
        public T Own<T>(T value) where T : UnityEngine.Object
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            AssertMainThread();
            UnityEngine.Object owned = value;
            var name = owned.name;
            Defer(async () => {
                // Unity's == reports destroyed objects as null; Destroy takes effect at the end of the frame.
                if (owned == null) return;
                UnityEngine.Object.Destroy(owned);
                await NextFrame();
                if (owned != null) throw new InvalidOperationException("Owned object " + name + " still exists after Destroy.");
            });
            return value;
        }

        /// <summary>Registers cleanup, run on the main thread in reverse registration order after the body.</summary>
        public void Defer(Func<Task> cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            if (cleaned) throw new InvalidOperationException("Cleanup already ran for this test.");
            cleanups.Add(cleanup);
        }

        public void Defer(Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            Defer(() => { cleanup(); return Task.CompletedTask; });
        }

        internal async Task<List<Exception>> CleanupAsync()
        {
            cleaned = true;
            var errors = new List<Exception>();
            for (int index = cleanups.Count - 1; index >= 0; index--) {
                try { await cleanups[index](); }
                catch (Exception error) { errors.Add(error); }
            }
            return errors;
        }
    }
}
