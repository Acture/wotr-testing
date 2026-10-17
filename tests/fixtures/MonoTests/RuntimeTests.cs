using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using WotR.Testing.Offline;
using Xunit;

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

    public sealed class RuntimeTests : IClassFixture<RuntimeReport>
    {
        [Fact]
        [Trait("Category", "Runtime")]
        public void UsesSelectedRuntime()
        {
            var scenario = Environment.GetEnvironmentVariable("WOTR_TEST_SCENARIO");
            if (scenario == "crash") Environment.Exit(7);
            if (scenario == "hang") Thread.Sleep(Timeout.Infinite);
            if (scenario == "settings") Assert.Equal("preserved", Environment.GetEnvironmentVariable("WOTR_TEST_SETTINGS_TOKEN"));
            Assert.NotEqual("failure", scenario);
            var expected = Environment.GetEnvironmentVariable("WOTR_TEST_EXPECT_RUNTIME") ?? "unity-mono";
            Assert.Equal(expected, Type.GetType("Mono.Runtime") != null ? "unity-mono" : "netfx");
            if (scenario == "json-isolation") {
                Assert.Contains(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "WotR.TestHost.Newtonsoft.Json");
                var inputs = File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(RuntimeTests).Assembly.Location), "wotr-offline-inputs.json"));
                var inputRoot = Regex.Match(inputs, "\"inputRoot\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                var gameJson = Assembly.LoadFrom(Path.Combine(inputRoot, "Wrath_Data", "Managed", "Newtonsoft.Json.dll"));
                Assert.NotNull(gameJson.GetType("Newtonsoft.Json.SerializationBinder"));
                Assert.Equal("Newtonsoft.Json", gameJson.GetName().Name);
                var manifest = XDocument.Load(Environment.GetEnvironmentVariable("WOTR_VSTEST_HOST_INFO")).Root;
                var originalDirectory = Path.GetDirectoryName((string)manifest.Attribute("originalHost"));
                var isolatedDirectory = Path.GetDirectoryName((string)manifest.Attribute("isolatedHost"));
                foreach (var file in manifest.Elements("File")) {
                    var name = (string)file.Attribute("name");
                    if (!(bool)file.Attribute("rewritten") || !name.EndsWith(".dll")) continue;
                    var originalName = name == "WotR.TestHost.Newtonsoft.Json.dll" ? "Newtonsoft.Json.dll" : name;
                    Assert.True(IlComparison.CompareMethodBodies(Path.Combine(originalDirectory, originalName), Path.Combine(isolatedDirectory, name)) > 0);
                }
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [Trait("Category", "Theory")]
        public void DiscoversTheoryRows(int value) => Assert.True(value > 0);

        [Fact(Skip = "[UNITY_RUNTIME_REQUIRED] synthetic native boundary")]
        public void NativeBoundaryIsSkipped() { }

        [Fact(Skip = "ordinary skip")]
        [Trait("Category", "UnexplainedSkip")]
        public void UnexplainedSkipFailsValidation() { }
    }
}
