using System;
using System.Linq;

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
    }
}
