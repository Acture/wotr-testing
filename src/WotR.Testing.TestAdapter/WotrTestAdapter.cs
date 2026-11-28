using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using WotR.Testing.Client;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.TestAdapter
{
    /// <summary>Lists the cases and fuzz targets of a .wotrtests manifest; in-game discovery reaches the game.</summary>
    [FileExtension(RunManifest.Extension)]
    [DefaultExecutorUri(WotrTestExecutor.Uri)]
    public sealed class WotrTestDiscoverer : ITestDiscoverer
    {
        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            foreach (var source in sources) {
                var run = new ManifestRun(source, true, null, message => logger.SendMessage(TestMessageLevel.Informational, message));
                var summary = run.Execute(CancellationToken.None);
                foreach (var partition in summary.Partitions)
                    foreach (var test in partition.Selected) discoverySink.SendTestCase(run.TestCase(partition.Partition, test));
                foreach (var problem in summary.Problems.Concat(summary.Partitions.SelectMany(partition => partition.Problems)))
                    logger.SendMessage(TestMessageLevel.Error, problem);
            }
        }
    }

    /// <summary>Runs a manifest's partitions and reports every case through VSTest, so loggers such as TRX see them.</summary>
    [ExtensionUri(Uri)]
    public sealed class WotrTestExecutor : ITestExecutor
    {
        public const string Uri = "executor://WotR/Testing";
        private readonly CancellationTokenSource canceled = new CancellationTokenSource();

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            foreach (var source in sources) Run(source, null, runContext, frameworkHandle);
        }

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            foreach (var group in tests.GroupBy(test => test.Source))
                Run(group.Key, new HashSet<Guid>(group.Select(test => test.Id)), runContext, frameworkHandle);
        }

        public void Cancel() => canceled.Cancel();

        private void Run(string source, HashSet<Guid> selected, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            var filter = runContext.GetTestCaseFilter(null, ManifestRun.Property);
            var run = new ManifestRun(source, false, frameworkHandle, message => frameworkHandle.SendMessage(TestMessageLevel.Informational, message)) {
                Selector = test => (selected == null || selected.Contains(test.Id)) && (filter == null || filter.MatchTestCase(test, name => ManifestRun.Value(test, name)))
            };
            var summary = run.Execute(canceled.Token);
            // Problems that fail the run appear as failed results, so every logger and the validation see them.
            var number = 0;
            foreach (var partition in summary.Partitions)
                foreach (var problem in partition.Problems) run.Problem("WotR." + partition.Partition.Mode + "." + partition.Partition.Kind + ".Problem" + number++, problem, partition.ReportFile);
            foreach (var problem in summary.Problems) run.Problem("WotR.Run.Problem" + number++, problem, summary.ReportFile);
        }
    }

    internal sealed class ManifestRun : IPartitionObserver
    {
        private static readonly TestProperty PartitionProperty = TestProperty.Register("WotR.Partition", "WotR partition", typeof(string), typeof(TestCase));
        private static readonly TestProperty IdProperty = TestProperty.Register("WotR.TestId", "WotR test id", typeof(string), typeof(TestCase));
        private readonly string source;
        private readonly bool listOnly;
        private readonly IFrameworkHandle handle;
        private readonly Action<string> log;
        private readonly Dictionary<string, TestCase> cases = new Dictionary<string, TestCase>(StringComparer.Ordinal);

        internal ManifestRun(string source, bool listOnly, IFrameworkHandle handle, Action<string> log)
        {
            this.source = source;
            this.listOnly = listOnly;
            this.handle = handle;
            this.log = log;
        }

        internal Func<TestCase, bool> Selector { get; set; }

        internal RunSummary Execute(CancellationToken cancellationToken)
        {
            RunManifest manifest;
            try { manifest = RunManifest.Read(source); }
            catch (Exception error) {
                var failed = new RunSummary();
                failed.Problems.Add("The WotR run manifest " + source + " cannot be read: " + error.Message);
                return failed;
            }
            var (plan, options) = manifest.Resolve(listOnly);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(manifest.Settings.RunTimeoutSeconds > 0 ? manifest.Settings.RunTimeoutSeconds : 900));
                var summary = RunCoordinator.RunAsync(plan, options, this, timeout.Token).GetAwaiter().GetResult();
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    summary.Problems.Add("The run exceeded its total timeout of " + manifest.Settings.RunTimeoutSeconds + " seconds.");
                return summary;
            }
        }

        public bool Select(Partition partition, TestDescriptor test) => Selector == null || Selector(TestCase(partition, test));

        public void Started(Partition partition, TestDescriptor test, TestStarted started) => handle?.RecordStart(TestCase(partition, test));

        public void Finished(Partition partition, TestDescriptor test, TestFinished finished)
        {
            if (handle == null) return;
            var testCase = TestCase(partition, test);
            var outcome = finished.Outcome == TestFinished.Passed ? TestOutcome.Passed : finished.Outcome == TestFinished.Skipped ? TestOutcome.Skipped : TestOutcome.Failed;
            var result = new TestResult(testCase) {
                Outcome = outcome, DisplayName = Display(partition, finished.DisplayName ?? test.DisplayName),
                ErrorMessage = outcome == TestOutcome.Passed ? null : finished.Message, ErrorStackTrace = finished.StackTrace,
                Duration = TimeSpan.FromMilliseconds(finished.DurationMilliseconds), EndTime = DateTimeOffset.Now
            };
            result.StartTime = result.EndTime - result.Duration;
            if (!string.IsNullOrEmpty(finished.Output)) result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, finished.Output));
            handle.RecordResult(result);
            handle.RecordEnd(testCase, outcome);
        }

        public void Log(string message) => log(message);

        internal void Problem(string name, string message, string report)
        {
            if (handle == null) return;
            var testCase = new TestCase(name, new Uri(WotrTestExecutor.Uri), source) { DisplayName = name, Id = Identity(name) };
            handle.RecordStart(testCase);
            handle.RecordResult(new TestResult(testCase) { Outcome = TestOutcome.Failed, ErrorMessage = message + (report == null ? "" : "\nReport: " + report) });
            handle.RecordEnd(testCase, TestOutcome.Failed);
        }

        internal TestCase TestCase(Partition partition, TestDescriptor test)
        {
            var key = partition.Name + "|" + test.Id;
            if (cases.TryGetValue(key, out var existing)) return existing;
            // The environment is part of the identity: a case declared for both runs once in each.
            var testCase = new TestCase(test.FullyQualifiedName, new Uri(WotrTestExecutor.Uri), source) {
                DisplayName = Display(partition, test.DisplayName), Id = Identity(key)
            };
            testCase.SetPropertyValue(PartitionProperty, partition.Name);
            testCase.SetPropertyValue(IdProperty, test.Id);
            foreach (var trait in test.Traits ?? new List<TestTrait>()) testCase.Traits.Add(trait.Name, trait.Value);
            cases[key] = testCase;
            return testCase;
        }

        private static string Display(Partition partition, string name) => name + " [" + partition.Mode + (partition.Kind == TestDescriptor.FuzzKind ? " fuzz" : "") + "]";

        private static Guid Identity(string key)
        {
            using (var sha = SHA256.Create()) {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return new Guid(hash.Take(16).ToArray());
            }
        }

        /// <summary>Filter properties: names, display names and traits.</summary>
        internal static TestProperty Property(string name)
        {
            switch (name) {
                case "FullyQualifiedName": return TestCaseProperties.FullyQualifiedName;
                case "DisplayName": return TestCaseProperties.DisplayName;
                default: return TestProperty.Find("WotR.Trait." + name) ?? TestProperty.Register("WotR.Trait." + name, name, typeof(string[]), typeof(TestCase));
            }
        }

        /// <summary>
        /// WotrMode reads as absent here: the build adds "WotrMode!=ingame" to the filter so the ordinary xUnit adapter
        /// leaves in-game cases alone, and that clause must not drop this adapter's in-game cases. Select environments
        /// with WotrTestMode, not with the filter.
        /// </summary>
        internal static object Value(TestCase test, string name)
        {
            switch (name) {
                case "FullyQualifiedName": return test.FullyQualifiedName;
                case "DisplayName": return test.DisplayName;
                case TestDescriptor.ModeTrait: return null;
                default:
                    var values = test.Traits.Where(trait => trait.Name == name).Select(trait => trait.Value).ToArray();
                    return values.Length == 0 ? null : values;
            }
        }
    }
}
