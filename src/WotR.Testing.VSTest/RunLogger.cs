using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace WotR.Testing.VSTest
{
    /// <summary>Records VSTest's native results for the post-run MSBuild validation, alongside any user-selected loggers.</summary>
    [FriendlyName("wotr")]
    [ExtensionUri("logger://WotR/Offline")]
    public sealed class RunLogger : ITestLoggerWithParameters
    {
        public void Initialize(TestLoggerEvents events, string directory)
            => throw new InvalidOperationException("The WotR logger requires the automatically generated runsettings.");

        public void Initialize(TestLoggerEvents events, Dictionary<string, string> parameters)
        {
            var path = parameters["ReportFile"];
            var root = new XElement("TestRun");
            var sync = new object();
            events.TestResult += (_, e) => {
                var result = e.Result;
                var message = string.Join("\n", new[] { result.ErrorMessage, result.ErrorStackTrace }
                    .Concat(result.Messages.Select(m => m.Text)));
                var classification = result.Outcome == TestOutcome.Passed ? "passed"
                    : result.Outcome == TestOutcome.Skipped && message.Contains("[UNITY_RUNTIME_REQUIRED]") ? "unity-runtime-required"
                    : result.Outcome == TestOutcome.Skipped ? "skipped-without-reason"
                    : message.Contains("[ENV_MISSING]") ? "environment-missing"
                    : message.Contains("[INIT_FAILED]") ? "initialization-failed"
                    : message.Contains("Xunit.Sdk.") || message.StartsWith("Assert.", StringComparison.Ordinal) ? "assertion-failed" : "error";
                // Results of the WotR adapter (in-game cases and fuzz targets) come from a .wotrtests manifest.
                var manifest = result.TestCase.Source?.EndsWith(".wotrtests", StringComparison.OrdinalIgnoreCase) == true;
                lock (sync) root.Add(new XElement("Test", new XAttribute("name", result.DisplayName ?? result.TestCase.DisplayName),
                    new XAttribute("outcome", result.Outcome), new XAttribute("classification", classification), new XAttribute("manifest", manifest), message));
            };
            events.TestRunMessage += (_, e) => {
                if (e.Level == TestMessageLevel.Error) lock (sync) root.Add(new XElement("Error", e.Message));
            };
            events.TestRunComplete += (_, e) => {
                lock (sync) {
                    root.SetAttributeValue("aborted", e.IsAborted);
                    root.SetAttributeValue("canceled", e.IsCanceled);
                    if (e.Error != null) root.Add(new XElement("Error", e.Error.ToString()));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    new XDocument(root).Save(path);
                }
            };
        }
    }
}
