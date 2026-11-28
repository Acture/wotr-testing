using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WotR.Testing.Fuzzing;
using WotR.Testing.InGame.Protocol;
using WotR.Testing.Runner;

namespace WotR.Testing.Runner.Fuzzing
{
    /// <summary>
    /// Registered fuzz targets as cases: one result per target, with the generated, shrunk or replayed calls inside it.
    /// Targets run through <see cref="RunnerEnvironment.RunInContext{T}"/>, on Unity's main thread in the player.
    /// </summary>
    public sealed class FuzzBridge : ITestBridge
    {
        private readonly Assembly assembly;
        private readonly Dictionary<string, FuzzTarget> targets = new Dictionary<string, FuzzTarget>(StringComparer.Ordinal);

        public FuzzBridge(Assembly assembly) => this.assembly = assembly;

        public IReadOnlyList<string> Diagnostics => Array.Empty<string>();

        public IReadOnlyList<TestDescriptor> Discover()
        {
            var descriptors = new List<TestDescriptor>();
            // Metadata only: no factory or options provider runs during discovery.
            foreach (var target in FuzzTargets.Discover(assembly)) {
                targets.Add(target.Id, target);
                descriptors.Add(new TestDescriptor {
                    Id = target.Id, DisplayName = target.DisplayName, Kind = TestDescriptor.FuzzKind,
                    FullyQualifiedName = target.Factory.DeclaringType.FullName.Replace('+', '.') + "." + target.Factory.Name,
                    Traits = Modes(target.Modes).Select(mode => new TestTrait { Name = TestDescriptor.ModeTrait, Value = mode }).ToList()
                });
            }
            return descriptors;
        }

        private static IEnumerable<string> Modes(WotrTestModes modes)
        {
            if (modes == WotrTestModes.Both) yield return "both";
            else if (modes == WotrTestModes.InGame) yield return Welcome.InGame;
            // Invalid registrations still appear offgame, where they fail with their error instead of vanishing.
            else yield return Welcome.OffGame;
        }

        public async Task<RunCounts> Run(IReadOnlyCollection<string> ids, Run request, RunnerEnvironment environment, EventWriter writer,
            TimeSpan testTimeout, CancellationToken cancellationToken)
        {
            var counts = new RunCounts();
            var selected = new HashSet<string>(ids, StringComparer.Ordinal);
            if (request.FuzzReplayCase != null && selected.Count != 1) throw new ArgumentException("A replay selects exactly one fuzz target.");
            foreach (var target in targets.Values.Where(target => selected.Contains(target.Id)).ToList()) {
                if (cancellationToken.IsCancellationRequested) {
                    counts.Error = "The run was canceled.";
                    break;
                }
                await writer.WriteAsync(new TestStarted { TestId = target.Id, DisplayName = target.DisplayName }).ConfigureAwait(false);
                var started = DateTime.UtcNow;
                var finished = new TestFinished { TestId = target.Id, DisplayName = target.DisplayName, Outcome = TestFinished.Failed };
                var truncated = false;
                try {
                    if (target.Error != null) throw new InvalidOperationException("Invalid fuzz registration: " + target.Error);
                    var work = environment.RunInContext(() => request.FuzzReplayCase != null ? target.ReplayAsync(request.FuzzReplayCase) : target.RunAsync(request.FuzzSeed));
                    if (await Task.WhenAny(work, Task.Delay(testTimeout, cancellationToken)).ConfigureAwait(false) != work) {
                        environment.MarkDirty(target.DisplayName + " did not finish within " + testTimeout.TotalSeconds + " seconds");
                        finished.Message = "[TIMEOUT] The fuzz target did not finish within " + testTimeout.TotalSeconds + " seconds. It cannot be stopped; restart the process before further runs.";
                    }
                    else Describe(await work.ConfigureAwait(false), finished, ref truncated);
                }
                catch (Exception error) {
                    finished.Message = MessageText.Limit(error.GetType().FullName + ": " + error.Message, ref truncated);
                    finished.StackTrace = MessageText.Limit(error.ToString(), ref truncated);
                }
                finished.DurationMilliseconds = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                finished.Cleanup = environment.Dirty == null ? SessionFinished.Clean : SessionFinished.Dirty;
                finished.Truncated = truncated;
                if (finished.Outcome == TestFinished.Passed) counts.Passed++;
                else counts.Failed++;
                await writer.WriteAsync(finished).ConfigureAwait(false);
                if (environment.Dirty != null) break;
            }
            return counts;
        }

        private static void Describe(FuzzReport report, TestFinished finished, ref bool truncated)
        {
            finished.Output = MessageText.Limit(report.ToJson(), ref truncated);
            finished.ReplayCase = report.ReplayCase;
            switch (report.Status) {
                case FuzzStatus.Passed when report.CallsAttempted > 0:
                    finished.Outcome = TestFinished.Passed;
                    break;
                case FuzzStatus.Passed:
                    finished.Message = "The fuzz target ran no calls.";
                    break;
                case FuzzStatus.AllSkipped:
                    // Every generated case was unavailable: nothing was checked, so this is not a pass.
                    finished.Message = "Every generated case was unavailable: " + string.Join("; ", report.SkippedReasons.Take(3));
                    break;
                default:
                    var failure = report.Failure;
                    finished.Message = MessageText.Limit((report.Status == FuzzStatus.Finding ? "Finding" : "Infrastructure failure (" + failure?.Kind + ")") +
                        (failure?.Method == null ? "" : " in " + failure.Method) + ": " + failure?.ExceptionType + ": " + failure?.Message +
                        " (seed " + report.Seed + ", " + report.ShrinkAttempts + " shrink attempts)", ref truncated);
                    finished.StackTrace = MessageText.Limit(failure?.ExceptionDetails, ref truncated);
                    break;
            }
        }
    }
}
