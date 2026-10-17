using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace WotR.Testing.Build
{
    public sealed class ValidateTestRun : Task
    {
        [Required] public string RunReport { get; set; }
        [Required] public string EnvironmentDirectory { get; set; }
        public int MinimumPassed { get; set; } = 1;
        [Required] public string Runtime { get; set; }

        public override bool Execute()
        {
            try {
                if (MinimumPassed < 1) throw new ArgumentException("WotrMinimumPassed must be at least one.");
                if (!File.Exists(RunReport)) throw new InvalidOperationException("Missing report: the WotR VSTest result logger did not finish.");
                var run = XDocument.Load(RunReport).Root;
                var tests = run.Elements("Test").ToArray();
                if (tests.Length == 0) Log.LogError("Zero tests: no test results were reported.");
                if ((string)run.Attribute("aborted") == "true" || (string)run.Attribute("canceled") == "true") Log.LogError("The test run was aborted or canceled.");
                foreach (var error in run.Elements("Error")) Log.LogError(error.Value);
                foreach (var test in tests.Where(t => (string)t.Attribute("classification") != "passed" && (string)t.Attribute("classification") != "unity-runtime-required"))
                    Log.LogError("{0}: {1}", (string)test.Attribute("name"), (string)test.Attribute("classification"));
                var passed = tests.Count(t => (string)t.Attribute("classification") == "passed");
                if (passed < MinimumPassed) Log.LogError("Too few passed tests: {0} passed, at least {1} required (skipped tests do not count).", passed, MinimumPassed);
                var environmentPath = Path.Combine(EnvironmentDirectory, "environment.json");
                if (!File.Exists(environmentPath)) Log.LogError("Missing report: environment.json was not written by the test fixture.");
                else {
                    XElement environment;
                    using (var stream = File.OpenRead(environmentPath))
                    using (var reader = JsonReaderWriterFactory.CreateJsonReader(stream, System.Xml.XmlDictionaryReaderQuotas.Max))
                        environment = XElement.Load(reader);
                    var actual = (string)environment.Element("process")?.Element("runtimeKind");
                    var expected = Runtime == "mono" ? "unity-mono" : "netfx";
                    if (actual != expected) Log.LogError("Runtime mismatch: expected {0}, environment.json reports {1}.", expected, actual ?? "no runtime");
                    var startupError = (string)environment.Element("startupError");
                    if (!string.IsNullOrEmpty(startupError)) Log.LogError("Initialization failed: {0}", startupError);
                    foreach (var warning in environment.Element("warnings")?.Elements() ?? Enumerable.Empty<XElement>())
                        Log.LogWarning(warning.Value);
                }
                Log.LogMessage(MessageImportance.High, "WotR reports: {0}", EnvironmentDirectory);
                return !Log.HasLoggedErrors;
            }
            catch (Exception error) { Log.LogErrorFromException(error, true); return false; }
        }
    }
}
