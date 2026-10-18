using System;
using System.IO;

namespace WotR.Testing.IntegrationFixture
{
    // Synthetic harness tests: no game initialization or mechanics are claimed here.
    public sealed class RuntimeReport : IDisposable
    {
        public void Dispose()
        {
            if (Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO") == "missing-report") return;
            var directory = Environment.GetEnvironmentVariable("WOTR_OFFLINE_REPORT_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var scenario = Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO");
            if (scenario == "invalid-report") {
                File.WriteAllText(Path.Combine(directory, "environment.json"), "not JSON");
                return;
            }
            File.WriteAllText(Path.Combine(directory, "environment.json"),
                "{\"synthetic\":true,\"process\":{\"runtimeKind\":\"" +
                (scenario == "wrong-report-runtime" ? "wrong-runtime" : Type.GetType("Mono.Runtime") != null ? "unity-mono" : "netfx") + "\"}}");
        }
    }
}
