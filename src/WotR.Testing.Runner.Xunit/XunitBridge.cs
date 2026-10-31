using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.InGame.Protocol;
using WotR.Testing.Runner;
using Xunit.Abstractions;
using Xunit.Sdk;
using TestFinished = WotR.Testing.InGame.Protocol.TestFinished;
using TestStarted = WotR.Testing.InGame.Protocol.TestStarted;

namespace WotR.Testing.Runner.Xunit
{
    /// <summary>Discovers and runs xUnit v2 cases in this process, keeping the discovered case objects for execution.</summary>
    public sealed class XunitBridge : ITestBridge
    {
        private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(5);
        private readonly Assembly assembly;
        private readonly Dictionary<string, IXunitTestCase> cases = new Dictionary<string, IXunitTestCase>(StringComparer.Ordinal);
        private readonly List<string> diagnostics = new List<string>();

        public XunitBridge(Assembly assembly) => this.assembly = assembly;

        public IReadOnlyList<string> Diagnostics => diagnostics;

        public IReadOnlyList<TestDescriptor> Discover()
        {
            var sink = new DiscoverySink();
            using (var framework = new XunitTestFramework(new DiagnosticSink(diagnostics)))
            using (var discoverer = framework.GetDiscoverer(Reflector.Wrap(assembly))) {
                discoverer.Find(false, sink, Options.ForDiscovery());
                if (!sink.Finished.Wait(DiscoveryTimeout)) throw new TimeoutException("xUnit discovery did not finish within " + DiscoveryTimeout + ".");
            }
            var descriptors = new List<TestDescriptor>();
            foreach (var testCase in sink.Cases) {
                if (!(testCase is IXunitTestCase xunitCase))
                    throw new NotSupportedException("Only xUnit v2 test cases are supported: " + testCase.DisplayName);
                if (cases.ContainsKey(testCase.UniqueID)) throw new InvalidOperationException("xUnit discovered two cases with the id of " + testCase.DisplayName);
                cases.Add(testCase.UniqueID, xunitCase);
                descriptors.Add(new TestDescriptor {
                    Id = testCase.UniqueID, DisplayName = testCase.DisplayName, Kind = TestDescriptor.TestKind,
                    FullyQualifiedName = testCase.TestMethod.TestClass.Class.Name + "." + testCase.TestMethod.Method.Name,
                    SkipReason = testCase.SkipReason,
                    Traits = testCase.Traits.SelectMany(trait => trait.Value.Select(value => new TestTrait { Name = trait.Key, Value = value })).ToList()
                });
            }
            return descriptors;
        }

        /// <summary>Runs the selected cases in discovery order, one at a time, streaming start and result events.</summary>
        public async Task<RunCounts> Run(IReadOnlyCollection<string> ids, Run request, RunnerEnvironment environment, EventWriter writer,
            TimeSpan testTimeout, CancellationToken cancellationToken)
        {
            var selected = new HashSet<string>(ids, StringComparer.Ordinal);
            var unknown = selected.Where(id => !cases.ContainsKey(id)).ToList();
            if (unknown.Count > 0) throw new ArgumentException("Unknown test ids for this discovery: " + string.Join(", ", unknown));
            var run = cases.Where(pair => selected.Contains(pair.Key)).Select(pair => pair.Value).ToList();
            var sink = new ExecutionSink(environment, writer, cancellationToken);
            using (var watchdog = new Timer(_ => sink.CheckTimeout(testTimeout), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
            using (var runner = new XunitTestAssemblyRunner(new TestAssembly(Reflector.Wrap(assembly), null, null), run,
                new DiagnosticSink(diagnostics), sink, Options.ForExecution())) {
                var finished = runner.RunAsync();
                // A timed-out test cannot be stopped; the session ends without waiting for it.
                await Task.WhenAny(finished, sink.Abandoned).ConfigureAwait(false);
                if (finished.IsFaulted) sink.Fail("xUnit failed: " + RunnerEnvironment.Describe(finished.Exception.GetBaseException()));
            }
            return sink.Counts;
        }

        private sealed class ExecutionSink : IMessageSink
        {
            private readonly RunnerEnvironment environment;
            private readonly EventWriter writer;
            private readonly CancellationToken cancellationToken;
            private readonly object gate = new object();
            private readonly TaskCompletionSource<bool> abandoned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private string current;
            private string currentName;
            private DateTime startedAt;
            private bool closed;

            internal ExecutionSink(RunnerEnvironment environment, EventWriter writer, CancellationToken cancellationToken)
            {
                this.environment = environment;
                this.writer = writer;
                this.cancellationToken = cancellationToken;
            }

            internal RunCounts Counts { get; } = new RunCounts();
            internal Task Abandoned => abandoned.Task;

            public bool OnMessage(IMessageSinkMessage message)
            {
                lock (gate) {
                    if (closed) return false;
                    try {
                        switch (message) {
                            case ITestStarting starting:
                                Start(starting.TestCase.UniqueID, starting.Test.DisplayName);
                                break;
                            case ITestResultMessage result:
                                Finish(result);
                                break;
                            case IErrorMessage error:
                                Fail("xUnit reported an error: " + ExceptionUtility.CombineMessages(error) + "\n" + ExceptionUtility.CombineStackTraces(error));
                                break;
                            // Fixture and class cleanup failures happen outside any single test.
                            case IFailureInformation failure when message is ITestCaseCleanupFailure || message is ITestClassCleanupFailure ||
                                message is ITestCollectionCleanupFailure || message is ITestAssemblyCleanupFailure ||
                                message is ITestMethodCleanupFailure || message is ITestCleanupFailure:
                                Fail("Cleanup failed: " + ExceptionUtility.CombineMessages(failure) + "\n" + ExceptionUtility.CombineStackTraces(failure));
                                break;
                        }
                    }
                    catch (Exception error) {
                        // The connection is gone; nothing further can be reported.
                        Close("Reporting failed: " + error.Message);
                    }
                    if (cancellationToken.IsCancellationRequested && Counts.Error == null) Counts.Error = "The run was canceled.";
                    return !closed && !cancellationToken.IsCancellationRequested && environment.Dirty == null;
                }
            }

            private void Start(string id, string name)
            {
                current = id;
                currentName = name;
                startedAt = DateTime.UtcNow;
                Write(new TestStarted { TestId = id, DisplayName = name });
            }

            private void Finish(ITestResultMessage result)
            {
                var id = result.TestCase.UniqueID;
                if (current != id) Start(id, result.Test.DisplayName);
                var finished = new TestFinished {
                    TestId = id, DisplayName = result.Test.DisplayName, DurationMilliseconds = (long)(result.ExecutionTime * 1000),
                    Cleanup = environment.Dirty == null ? SessionFinished.Clean : SessionFinished.Dirty
                };
                var truncated = false;
                switch (result) {
                    case ITestPassed _:
                        finished.Outcome = TestFinished.Passed;
                        Counts.Passed++;
                        break;
                    case ITestFailed failed:
                        finished.Outcome = TestFinished.Failed;
                        finished.Message = MessageText.Limit(ExceptionUtility.CombineMessages(failed), ref truncated);
                        finished.StackTrace = MessageText.Limit(ExceptionUtility.CombineStackTraces(failed), ref truncated);
                        Counts.Failed++;
                        break;
                    case ITestSkipped skipped:
                        finished.Outcome = TestFinished.Skipped;
                        finished.Message = MessageText.Limit(skipped.Reason, ref truncated);
                        Counts.Skipped++;
                        break;
                    default:
                        throw new NotSupportedException("Unknown xUnit result " + result.GetType().Name);
                }
                if (environment.Dirty != null)
                    finished.Message = (finished.Message == null ? "" : finished.Message + "\n") + "The session is dirty: " + environment.Dirty;
                finished.Output = MessageText.Limit(result.Output, ref truncated);
                finished.Truncated = truncated;
                current = null;
                Write(finished);
            }

            internal void CheckTimeout(TimeSpan timeout)
            {
                lock (gate) {
                    if (closed || current == null || DateTime.UtcNow - startedAt < timeout) return;
                    environment.MarkDirty(currentName + " did not finish within " + timeout.TotalSeconds + " seconds");
                    try {
                        Write(new TestFinished {
                            TestId = current, DisplayName = currentName, Outcome = TestFinished.Failed, Cleanup = SessionFinished.Dirty,
                            DurationMilliseconds = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                            Message = "[TIMEOUT] The test did not finish within " + timeout.TotalSeconds + " seconds. It cannot be stopped; restart the process before further runs."
                        });
                        Counts.Failed++;
                    }
                    catch (Exception) { }
                    Close("A test timed out.");
                }
            }

            internal void Fail(string error)
            {
                lock (gate) Counts.Error = Counts.Error == null ? error : Counts.Error + "\n" + error;
            }

            private void Close(string error)
            {
                closed = true;
                Fail(error);
                abandoned.TrySetResult(true);
            }

            private void Write(Event message) => writer.WriteAsync(message).GetAwaiter().GetResult();
        }

        private sealed class DiscoverySink : IMessageSink
        {
            internal readonly List<ITestCase> Cases = new List<ITestCase>();
            internal readonly ManualResetEventSlim Finished = new ManualResetEventSlim();

            public bool OnMessage(IMessageSinkMessage message)
            {
                if (message is ITestCaseDiscoveryMessage discovered) lock (Cases) Cases.Add(discovered.TestCase);
                else if (message is IDiscoveryCompleteMessage) Finished.Set();
                return true;
            }
        }

        private sealed class DiagnosticSink : IMessageSink
        {
            private readonly List<string> messages;
            internal DiagnosticSink(List<string> messages) => this.messages = messages;

            public bool OnMessage(IMessageSinkMessage message)
            {
                if (message is IDiagnosticMessage diagnostic) lock (messages) messages.Add(diagnostic.Message);
                return true;
            }
        }

        private sealed class Options : ITestFrameworkDiscoveryOptions, ITestFrameworkExecutionOptions
        {
            private readonly Dictionary<string, object> values = new Dictionary<string, object>();

            internal static Options ForDiscovery()
            {
                var options = new Options();
                // Theory rows become separate cases when their data can be enumerated at discovery.
                options.SetValue("xunit.discovery.PreEnumerateTheories", true);
                return options;
            }

            internal static Options ForExecution()
            {
                // One test at a time: a player has one session, and reporting order must follow execution.
                var options = new Options();
                options.SetValue("xunit.execution.DisableParallelization", true);
                options.SetValue("xunit.execution.MaxParallelThreads", 1);
                options.SetValue("xunit.execution.SynchronousMessageReporting", true);
                return options;
            }

            public TValue GetValue<TValue>(string name) => values.TryGetValue(name, out var value) ? (TValue)value : default;
            public void SetValue<TValue>(string name, TValue value) => values[name] = value;
        }
    }
}
