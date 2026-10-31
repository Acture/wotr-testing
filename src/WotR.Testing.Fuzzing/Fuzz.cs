using System;
using System.Linq;
using System.Threading.Tasks;

namespace WotR.Testing.Fuzzing
{
    public static class Fuzz
    {
        /// <summary>Generates and executes cases, throwing with the report on any unsuccessful run.</summary>
        public static FuzzReport Check<TApi>(Func<TApi> factory, FuzzOptions options = null)
        {
            var report = Run(factory, options);
            if (!report.Succeeded) throw new FuzzFailureException(report);
            return report;
        }

        /// <summary>Exercises the explicitly registered interface; unexpected exceptions are findings.</summary>
        public static FuzzReport Run<TApi>(Func<TApi> factory, FuzzOptions options = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var engine = new FuzzEngine(typeof(TApi), options);
            return engine.Run(() => factory());
        }

        /// <summary>Executes a saved case exactly once, without random generation or shrinking.</summary>
        public static FuzzReport Replay<TApi>(Func<TApi> factory, string serializedCase, FuzzOptions options = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var engine = new FuzzEngine(typeof(TApi), options);
            return engine.Replay(() => factory(), serializedCase);
        }

        /// <summary>
        /// Like <see cref="Run{TApi}"/>, but awaits the factory and every Task-returning call without
        /// <c>ConfigureAwait(false)</c>, so target code resumes on the caller's synchronization context.
        /// Configuration errors are thrown before any target is constructed.
        /// </summary>
        public static Task<FuzzReport> RunAsync<TApi>(Func<Task<TApi>> factory, FuzzOptions options = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var engine = new FuzzEngine(typeof(TApi), options);
            return engine.RunAsync(() => FuzzEngine.Untyped(factory()));
        }

        /// <summary>Like <see cref="RunAsync{TApi}"/>; the task faults with <see cref="FuzzFailureException"/> on any unsuccessful run.</summary>
        public static Task<FuzzReport> CheckAsync<TApi>(Func<Task<TApi>> factory, FuzzOptions options = null)
            => Succeeded(RunAsync(factory, options));

        /// <summary>Like <see cref="Replay{TApi}"/>, awaiting on the caller's synchronization context.</summary>
        public static Task<FuzzReport> ReplayAsync<TApi>(Func<Task<TApi>> factory, string serializedCase, FuzzOptions options = null)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var engine = new FuzzEngine(typeof(TApi), options);
            return engine.ReplayAsync(() => FuzzEngine.Untyped(factory()), serializedCase);
        }

        private static async Task<FuzzReport> Succeeded(Task<FuzzReport> run)
        {
            var report = await run;
            if (!report.Succeeded) throw new FuzzFailureException(report);
            return report;
        }
    }
}
