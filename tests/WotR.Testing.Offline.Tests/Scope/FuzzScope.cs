using System;
using System.IO;
using WotR.Testing.Fuzzing;

namespace WotR.Testing.Offline.Tests
{
    /// <summary>
    /// Runs a fuzz target in the offgame scope. A failed run writes its report next to the run report, so the
    /// minimized input can be replayed without the original console output.
    /// </summary>
    internal static class FuzzScope
    {
        internal static string ReportDirectory()
        {
            var directory = Environment.GetEnvironmentVariable("WOTR_OFFLINE_REPORT_DIR");
            return string.IsNullOrEmpty(directory)
                ? Path.Combine(Path.GetDirectoryName(typeof(FuzzScope).Assembly.Location), "wotr-reports")
                : directory;
        }

        internal static FuzzReport Run<TApi>(string name, Func<TApi> factory, FuzzOptions options)
        {
            try { return Fuzz.Check(factory, options); }
            catch (FuzzFailureException failure)
            {
                var directory = ReportDirectory();
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "fuzz-" + name + ".json"), failure.Report.ToJson());
                throw;
            }
        }
    }
}
