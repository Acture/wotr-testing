using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using WotR.Testing.Client;
using WotR.Testing.InGame.Protocol;
using Xunit;

namespace WotR.Testing.InGame.Tests;

public class TestScopeTests
{
    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "ScopeSamples.dll");

    [Fact]
    public void Counts_declared_cases_and_targets_per_partition_from_metadata()
    {
        var scope = TestScope.Scan(Samples);
        // Untagged Fact, Theory (one method), FsCheck property, custom Fact subclass, both, explicit offgame and the
        // misspelled case, which counts as untagged besides being an error.
        Assert.Equal(7, scope.Count(Welcome.OffGame, TestDescriptor.TestKind));
        Assert.Equal(3, scope.Count(Welcome.InGame, TestDescriptor.TestKind));
        Assert.Equal(2, scope.Count(Welcome.OffGame, TestDescriptor.FuzzKind));
        Assert.Equal(2, scope.Count(Welcome.InGame, TestDescriptor.FuzzKind));
    }

    [Fact]
    public void A_misspelled_mode_is_an_error_rather_than_a_case_that_never_runs()
    {
        var error = Assert.Single(TestScope.Scan(Samples).Errors);
        Assert.Contains("WrongCase", error);
        Assert.Contains("inGame", error);
        Assert.Throws<PlanException>(() => ExecutionPlan.Resolve(TestScope.Scan(Samples), null, null, null, null, "mono", false));
    }
}

public class ExecutionPlanTests
{
    private static TestScope Scope(params string[] partitions)
    {
        var scope = new TestScope();
        foreach (var partition in partitions) {
            var parts = partition.Split('/');
            scope.Add(parts[0], parts[1]);
        }
        return scope;
    }

    private static string[] Names(ExecutionPlan plan) => plan.Partitions.Select(partition => partition.Name).ToArray();

    [Fact]
    public void Defaults_run_every_declared_partition()
    {
        var plan = ExecutionPlan.Resolve(Scope("offgame/tests", "ingame/tests", "offgame/fuzz", "ingame/fuzz"), null, null, null, null, "mono", false);
        Assert.Equal(new[] { "offgame/tests", "offgame/fuzz", "ingame/tests", "ingame/fuzz" }, Names(plan));
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void Existing_offgame_projects_get_only_the_offgame_partition()
        => Assert.Equal(new[] { "offgame/tests" }, Names(ExecutionPlan.Resolve(Scope("offgame/tests"), "auto", "all", null, null, "mono", false)));

    [Theory]
    [InlineData("ingame", "all", "ingame/tests,ingame/fuzz")]
    [InlineData("auto", "fuzz", "offgame/fuzz,ingame/fuzz")]
    [InlineData("auto", "tests", "offgame/tests,ingame/tests")]
    [InlineData("ingame", "fuzz", "ingame/fuzz")]
    public void Selectors_only_narrow_the_scope(string mode, string kind, string expected)
    {
        var plan = ExecutionPlan.Resolve(Scope("offgame/tests", "ingame/tests", "offgame/fuzz", "ingame/fuzz"), mode, kind, null, null, "mono", false);
        Assert.Equal(expected.Split(','), Names(plan));
        Assert.Equal(4 - plan.Partitions.Count, plan.Excluded.Count);
    }

    [Theory]
    [InlineData("ingame", "tests", null, "mono", false)]     // no in-game tests declared
    [InlineData("auto", "bogus", null, "mono", false)]
    [InlineData("auto", "tests", 5, "mono", false)]         // a fuzz seed with tests only
    [InlineData("ingame", "all", null, "netfx", false)]     // an offgame runtime for an in-game run
    [InlineData("offgame", "all", null, "mono", true)]      // in-game options for an offgame run
    public void Contradictory_or_empty_requests_fail(string mode, string kind, int? seed, string runtime, bool inGameOptions)
        => Assert.Throws<PlanException>(() => ExecutionPlan.Resolve(Scope("offgame/tests", "ingame/fuzz"), mode, kind, seed, null, runtime, inGameOptions));

    [Fact]
    public void A_replay_selects_its_recorded_environment_and_fuzz_only()
    {
        var file = Path.Combine(Path.GetTempPath(), "wotr-replay-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{\"FormatVersion\": 1, \"Target\": \"A:B.C:D\", \"Mode\": \"ingame\", \"Case\": \"{}\"}");
        try {
            var scope = Scope("offgame/tests", "offgame/fuzz", "ingame/fuzz");
            var plan = ExecutionPlan.Resolve(scope, null, null, null, file, "mono", false);
            Assert.Equal(new[] { "ingame/fuzz" }, Names(plan));
            Assert.Equal("A:B.C:D", plan.Replay.Target);
            Assert.Throws<PlanException>(() => ExecutionPlan.Resolve(scope, "offgame", null, null, file, "mono", false));
            Assert.Throws<PlanException>(() => ExecutionPlan.Resolve(scope, null, "tests", null, file, "mono", false));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("{\"FormatVersion\": 2, \"Target\": \"t\", \"Mode\": \"ingame\", \"Case\": \"{}\"}")]
    [InlineData("{\"FormatVersion\": 1, \"Target\": \"t\", \"Mode\": \"elsewhere\", \"Case\": \"{}\"}")]
    [InlineData("{\"FormatVersion\": 1, \"Mode\": \"ingame\", \"Case\": \"{}\"}")]
    [InlineData("not json")]
    public void Invalid_replay_files_are_rejected(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), "wotr-replay-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, content);
        try { Assert.Throws<PlanException>(() => ReplaySelection.Read(file)); }
        finally { File.Delete(file); }
    }
}

public class ConnectionModeTests
{
    [Theory]
    [InlineData(null, 0, "launch")]
    [InlineData("", 0, "launch")]
    [InlineData("attach", 1234, "attach")]
    [InlineData("auto", 0, "auto")]
    public void A_run_starts_its_own_game_unless_attach_or_auto_is_chosen(string mode, int processId, string expected)
        => Assert.Equal(expected, ConnectionModes.Resolve(mode, processId));

    [Theory]
    [InlineData("launch", 1234)]   // a running game to use, for a run that starts its own
    [InlineData("sometimes", 0)]
    public void Connection_choices_a_run_cannot_honor_fail_before_it_starts(string mode, int processId)
        => Assert.Throws<PlanException>(() => ConnectionModes.Resolve(mode, processId));
}

public class ReportTests
{
    [Fact]
    public void Report_json_escapes_text_and_embeds_host_json()
    {
        var text = JsonText.Write(new Dictionary<string, object> {
            ["name"] = "quote \" slash \\ line\nend", ["count"] = 3, ["flag"] = true, ["none"] = null,
            ["list"] = new List<object> { 1, "two" }, ["host"] = new JsonText.Raw("{\"a\":1}"), ["empty"] = new JsonText.Raw(null)
        });
        var report = ProtocolJson.Deserialize<Sample>(text);
        Assert.Equal("quote \" slash \\ line\nend", report.Name);
        Assert.Equal(3, report.Count);
        Assert.Contains("\"host\": {\"a\":1}", text);
        Assert.Contains("\"empty\": null", text);
    }

    [Fact]
    public void Manifest_round_trip_keeps_the_resolved_plan()
    {
        var file = Path.Combine(Path.GetTempPath(), "wotr-" + Guid.NewGuid().ToString("N") + RunManifest.Extension);
        try {
            new RunManifest {
                Settings = new RunSettings { TestAssembly = "C:/t/Tests.dll", Runtime = "mono", Connection = "attach", TestTimeoutSeconds = 7, FuzzSeed = 9 },
                RequestedMode = "auto", RequestedKind = "all", ReportDirectory = "C:/r", RunId = "run",
                Partitions = new List<ManifestPartition> { new ManifestPartition { Mode = "ingame", Kind = "fuzz", Declared = 2 } },
                Excluded = new List<ManifestPartition> { new ManifestPartition { Mode = "offgame", Kind = "fuzz", Declared = 1 } },
                RunByVSTest = new List<ManifestPartition> { new ManifestPartition { Mode = "offgame", Kind = "tests", Declared = 3 } },
                SupportFiles = new List<string> { "C:/f/System.Runtime.dll" }, ExpectedModules = new List<ModuleIdentity> { new ModuleIdentity { Name = "Mod", Mvid = "m" } }
            }.Write(file);
            var (plan, options) = RunManifest.Read(file).Resolve(false);
            Assert.Equal("ingame/fuzz", Assert.Single(plan.Partitions).Name);
            Assert.Equal("offgame/fuzz", Assert.Single(plan.Excluded).Name);
            Assert.Equal(3, Assert.Single(plan.RunByVSTest).Declared);
            Assert.Equal(9, plan.FuzzSeed);
            Assert.Equal("attach", options.Connection);
            Assert.Equal(7, options.Settings.TestTimeoutSeconds);
            Assert.Equal("Mod", Assert.Single(options.Settings.ExpectedModules).Name);
            Assert.Single(options.Settings.SupportFiles);
        }
        finally { File.Delete(file); }
    }

    [System.Runtime.Serialization.DataContract]
    private sealed class Sample
    {
        [System.Runtime.Serialization.DataMember(Name = "name")] public string Name { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "count")] public int Count { get; set; }
    }
}

public class HostClientTests
{
    [Fact]
    public async Task Handshake_needs_the_token_and_silence_fails_instead_of_hanging()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = Task.Run(async () => {
            using var connection = await listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var hello = Assert.IsType<Hello>(await MessageFraming.ReadAsync(stream));
            Assert.Equal("secret", hello.Token);
            await new EventWriter(stream, hello.SessionId).WriteAsync(new Welcome { Mode = Welcome.InGame, ProcessId = 42 });
            // Read the lease, then say nothing: no reply and no heartbeat.
            Assert.IsType<Lease>(await MessageFraming.ReadAsync(stream));
            await Task.Delay(TimeSpan.FromSeconds(5));
        });
        using (var client = await HostClient.ConnectAsync(port, "secret", "run", "test", TimeSpan.FromSeconds(5))) {
            Assert.Equal(42, client.Host.ProcessId);
            client.IdleTimeout = TimeSpan.FromSeconds(1);
            var error = await Assert.ThrowsAsync<ProtocolException>(() => client.LeaseAsync(new Lease { Kind = TestDescriptor.TestKind }));
            Assert.Equal(ProtocolException.Disconnected, error.Code);
        }
        await host;
        listener.Stop();
    }
}
