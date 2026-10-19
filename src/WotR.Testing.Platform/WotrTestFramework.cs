using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Testing.Extensions.TrxReport.Abstractions;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Requests;
using WotR.Testing.Client;
using WotR.Testing.InGame.Protocol;

namespace WotR.Testing.Platform
{
    /// <summary>
    /// Plans the run from metadata, then runs each partition through an offgame worker or the in-game host and reports
    /// every case as a test node. Problems that fail the run appear as failed nodes: `dotnet test` does not show a
    /// test application's own error output.
    /// </summary>
    internal sealed class WotrTestFramework : ITestFramework, IDataProducer
    {
        private readonly ICommandLineOptions options;

        public WotrTestFramework(ICommandLineOptions options) => this.options = options;

        public string Uid => nameof(WotrTestFramework);
        public string Version => typeof(WotrTestFramework).Assembly.GetName().Version.ToString();
        public string DisplayName => "WotR.Testing";
        public string Description => "Offgame and in-game tests and fuzz targets for WotR mods.";
        public Type[] DataTypesProduced => new[] { typeof(TestNodeUpdateMessage) };
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);

        public Task<CreateTestSessionResult> CreateTestSessionAsync(CreateTestSessionContext context)
            => Task.FromResult(new CreateTestSessionResult { IsSuccess = true });

        public Task<CloseTestSessionResult> CloseTestSessionAsync(CloseTestSessionContext context)
            => Task.FromResult(new CloseTestSessionResult { IsSuccess = true });

        public async Task ExecuteRequestAsync(ExecuteRequestContext context)
        {
            var publisher = new Publisher(this, context);
            try {
                var discovery = context.Request is DiscoverTestExecutionRequest;
                if (!options.TryGetOptionArgumentList(WotrOptions.Settings, out var settingsPath))
                    throw new PlanException("No WotR run settings. Reference WotR.Testing and run through `dotnet test` with the Microsoft.Testing.Platform runner.");
                var settings = RunSettings.Read(settingsPath[0]);
                var kind = options.IsOptionSet(WotrOptions.Fuzz) ? TestDescriptor.FuzzKind : options.IsOptionSet(WotrOptions.Tests) ? TestDescriptor.TestKind : null;
                var (plan, run) = RunSetup.Create(settings, Argument(WotrOptions.Mode), kind,
                    Argument(WotrOptions.FuzzSeed) is string seed ? int.Parse(seed, CultureInfo.InvariantCulture) : (int?)null,
                    Argument(WotrOptions.FuzzReplay), discovery, RunSetup.NewRunId());
                var observer = new Observer(publisher, (context.Request as TestExecutionRequest)?.Filter, discovery, Path.GetFileNameWithoutExtension(settings.TestAssembly));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)) {
                    timeout.CancelAfter(TimeSpan.FromSeconds(settings.RunTimeoutSeconds > 0 ? settings.RunTimeoutSeconds : 900));
                    var summary = await RunCoordinator.RunAsync(plan, run, observer, timeout.Token);
                    if (timeout.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
                        summary.Problems.Add("The run exceeded its total timeout of " + settings.RunTimeoutSeconds + " seconds.");
                    if (discovery)
                        foreach (var partition in summary.Partitions)
                            foreach (var test in partition.Selected)
                                publisher.Publish(observer.Node(partition.Partition, test, DiscoveredTestNodeStateProperty.CachedInstance));
                    var number = 0;
                    foreach (var partition in summary.Partitions)
                        foreach (var problem in partition.Problems) publisher.Problem("wotr/" + partition.Partition.Name + "/problem/" + number++, "WotR " + partition.Partition.Name, problem, partition.ReportFile);
                    foreach (var problem in summary.Problems) publisher.Problem("wotr/run/problem/" + number++, "WotR run", problem, summary.ReportFile);
                }
            }
            catch (Exception error) when (error is PlanException || error is ProtocolException || error is IOException) {
                publisher.Problem("wotr/configuration", "WotR configuration", error.Message, null);
            }
            catch (Exception error) {
                publisher.Problem("wotr/error", "WotR runner", error.ToString(), null);
            }
            finally {
                await publisher.Completion;
                context.Complete();
            }
        }

        private string Argument(string name) => options.TryGetOptionArgumentList(name, out var values) ? values[0] : null;

        /// <summary>Publishes in callback order; observer callbacks arrive on the protocol reading thread.</summary>
        private sealed class Publisher
        {
            private readonly IDataProducer producer;
            private readonly ExecuteRequestContext context;
            private readonly object gate = new object();
            private Task last = Task.CompletedTask;

            internal Publisher(IDataProducer producer, ExecuteRequestContext context)
            {
                this.producer = producer;
                this.context = context;
            }

            internal Task Completion { get { lock (gate) return last; } }

            internal void Publish(TestNode node)
            {
                lock (gate) last = last.ContinueWith(_ => context.MessageBus.PublishAsync(producer,
                    new TestNodeUpdateMessage(context.Request.Session.SessionUid, node)), TaskScheduler.Default).Unwrap();
            }

            internal void Problem(string uid, string name, string message, string report)
                => Publish(new TestNode {
                    Uid = uid, DisplayName = name,
                    Properties = new PropertyBag(new FailedTestNodeStateProperty(message + (report == null ? "" : "\nReport: " + report)))
                });
        }

        private sealed class Observer : IPartitionObserver
        {
            private readonly Publisher publisher;
            private readonly ITestExecutionFilter filter;
            private readonly bool discovery;
            private readonly string assembly;

            internal Observer(Publisher publisher, ITestExecutionFilter filter, bool discovery, string assembly)
            {
                this.publisher = publisher;
                this.filter = filter;
                this.discovery = discovery;
                this.assembly = assembly;
            }

            public bool Select(Partition partition, TestDescriptor test)
            {
                switch (filter) {
                    case TestNodeUidListFilter uids: return uids.TestNodeUids.Any(uid => uid.Value == Uid(partition, test));
#pragma warning disable TPEXP // The platform's tree filter is marked experimental; --treenode-filter needs it.
                    case TreeNodeFilter tree: return tree.MatchesFilter(Path(test), Filterable(partition, test));
#pragma warning restore TPEXP
                    default: return true;
                }
            }

            public void Started(Partition partition, TestDescriptor test, TestStarted started)
            {
                if (!discovery) publisher.Publish(Node(partition, test, InProgressTestNodeStateProperty.CachedInstance, started.DisplayName));
            }

            public void Finished(Partition partition, TestDescriptor test, TestFinished finished)
            {
                IProperty state;
                switch (finished.Outcome) {
                    case TestFinished.Passed: state = PassedTestNodeStateProperty.CachedInstance; break;
                    case TestFinished.Skipped: state = new SkippedTestNodeStateProperty(finished.Message ?? "skipped"); break;
                    default: state = new FailedTestNodeStateProperty(new RemoteFailure(finished.Message ?? "failed", finished.StackTrace), finished.Message ?? "failed"); break;
                }
                var node = Node(partition, test, state, finished.DisplayName);
                var end = DateTimeOffset.UtcNow;
                var duration = TimeSpan.FromMilliseconds(finished.DurationMilliseconds);
                node.Properties.Add(new TimingProperty(new TimingInfo(end - duration, end, duration)));
                if (!string.IsNullOrEmpty(finished.Output)) node.Properties.Add(new StandardOutputProperty(finished.Output));
                publisher.Publish(node);
            }

            public void Log(string message) => Console.Error.WriteLine(message);

            internal TestNode Node(Partition partition, TestDescriptor test, IProperty state, string displayName = null)
            {
                var (ns, type, method) = Split(test.FullyQualifiedName);
                var properties = new PropertyBag(state,
                    new TestMethodIdentifierProperty(assembly, ns, type, method, 0, Array.Empty<string>(), "System.Void"),
                    new TrxFullyQualifiedTypeNameProperty(ns.Length == 0 ? type : ns + "." + type));
                foreach (var trait in test.Traits ?? new List<TestTrait>()) properties.Add(new TestMetadataProperty(trait.Name, trait.Value));
                // The environment is part of the result's identity: a case declared for both runs once in each.
                return new TestNode {
                    Uid = Uid(partition, test),
                    DisplayName = (displayName ?? test.DisplayName) + " [" + partition.Mode + (partition.Kind == TestDescriptor.FuzzKind ? " fuzz" : "") + "]",
                    Properties = properties
                };
            }

            private static string Uid(Partition partition, TestDescriptor test) => partition.Mode + "/" + partition.Kind + "/" + test.Id;

            private string Path(TestDescriptor test)
            {
                var (ns, type, method) = Split(test.FullyQualifiedName);
                return "/" + Escape(assembly) + "/" + Escape(ns) + "/" + Escape(type) + "/" + Escape(method);
            }

            private static PropertyBag Filterable(Partition partition, TestDescriptor test)
            {
                var properties = new PropertyBag(new TestMetadataProperty("WotrPartition", partition.Name));
                foreach (var trait in test.Traits ?? new List<TestTrait>()) properties.Add(new TestMetadataProperty(trait.Name, trait.Value));
                return properties;
            }

            private static string Escape(string fragment) => fragment.Replace("/", "\\/");

            private static (string Namespace, string Type, string Method) Split(string fullyQualifiedName)
            {
                var method = fullyQualifiedName.LastIndexOf('.');
                var typeName = method < 0 ? "" : fullyQualifiedName.Substring(0, method);
                var ns = typeName.LastIndexOf('.');
                return (ns < 0 ? "" : typeName.Substring(0, ns), ns < 0 ? typeName : typeName.Substring(ns + 1), fullyQualifiedName.Substring(method + 1));
            }
        }

        /// <summary>A failure from another process: its message and stack trace as reported there.</summary>
        private sealed class RemoteFailure : Exception
        {
            private readonly string stack;
            internal RemoteFailure(string message, string stack) : base(message) => this.stack = stack;
            public override string StackTrace => stack;
        }
    }
}
