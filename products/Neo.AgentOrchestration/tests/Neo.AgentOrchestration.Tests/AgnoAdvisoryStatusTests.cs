using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class AgnoAdvisoryStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Optional_advisory_is_scoped_and_cannot_enable_task_execution()
    {
        using var f = new Fixture(); f.Write();
        var catalog = await f.Reader().ReadAsync(f.Scope, f.Project, Ct);
        Assert.Equal(4, catalog.Runtimes.Count);
        var advisory = Assert.Single(catalog.Runtimes, x => x.Engine == "agno");
        Assert.True(advisory.Installed); Assert.True(advisory.TransportHealthy);
        Assert.False(advisory.ExecutionReady); Assert.Equal("3.1.3", advisory.Version);
        Assert.Equal("advisory-healthy", advisory.Reason);
        Assert.DoesNotContain(f.Path, JsonSerializer.Serialize(catalog));
        var foreign = await f.Reader().ReadAsync(new(Guid.NewGuid(), f.Scope.WorkspaceId), f.Project, Ct);
        Assert.DoesNotContain(foreign.Runtimes, x => x.Engine == "agno");
        var other = await f.Reader().ReadAsync(f.Scope, Guid.NewGuid(), Ct);
        Assert.DoesNotContain(other.Runtimes, x => x.Engine == "agno");
    }

    [Theory]
    [InlineData(-181)] [InlineData(16)]
    public async Task Stale_or_future_advisory_cannot_claim_health(int age)
    {
        using var f = new Fixture(); f.Write(age);
        var result = await f.Reader().ReadAsync(f.Scope, f.Project, Ct);
        var advisory = Assert.Single(result.Runtimes, x => x.Engine == "agno");
        Assert.Equal("report-stale", advisory.Reason); Assert.False(advisory.ExecutionReady);
        Assert.False(advisory.TransportHealthy);
    }

    [Theory]
    [InlineData("{\"schema\":\"fanasa-agno-advisory/v1\",\"token\":\"private\"}")]
    [InlineData("{\"schema\":\"one\",\"Schema\":\"two\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\":\"fanasa-agno-advisory/v1\",\"executionReady\":true}")]
    public async Task Invalid_private_report_never_leaks_or_changes_native_health(string body)
    {
        using var f = new Fixture(); File.WriteAllText(f.Path, body);
        var catalog = await f.Reader().ReadAsync(f.Scope, f.Project, Ct);
        var advisory = Assert.Single(catalog.Runtimes, x => x.Engine == "agno");
        Assert.False(advisory.Installed); Assert.False(advisory.ExecutionReady);
        Assert.Equal(3, catalog.Runtimes.Count(x => x.Engine != "agno"));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(catalog));
    }

    [Fact]
    public async Task Missing_oversized_relative_and_ambiguous_config_fail_closed()
    {
        using var f = new Fixture();
        Assert.False((await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes.Single(x => x.Engine == "agno").Installed);
        File.WriteAllText(f.Path, new string('x', 8193));
        Assert.False((await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes.Single(x => x.Engine == "agno").Installed);
        var config = f.Settings(); config["AgentRuntimeStatus:Bindings:test:AdvisoryStatusFile"] = "status.json";
        Assert.False((await f.Reader(config).ReadAsync(f.Scope, f.Project, Ct)).Runtimes.Single(x => x.Engine == "agno").Installed);
        config = f.Settings(); foreach (var entry in f.Settings()) config.Add(entry.Key.Replace(":test:", ":other:"), entry.Value);
        Assert.DoesNotContain((await f.Reader(config).ReadAsync(f.Scope, f.Project, Ct)).Runtimes, x => x.Engine == "agno");
        config = f.Settings(); config.Remove("AgentRuntimeStatus:Bindings:test:AdvisoryStatusFile");
        Assert.Equal(3, (await f.Reader(config).ReadAsync(f.Scope, f.Project, Ct)).Runtimes.Count);
    }

    [Theory]
    [InlineData("coding", "3.1.3", true, true, "advisory-healthy")]
    [InlineData("advisory-only", null, true, true, "advisory-healthy")]
    [InlineData("advisory-only", "private/path", true, true, "advisory-healthy")]
    [InlineData("advisory-only", "3.1.3", false, true, "advisory-healthy")]
    [InlineData("advisory-only", "3.1.3", true, true, "not-installed")]
    [InlineData("advisory-only", "3.1.3", true, false, "advisory-healthy")]
    public async Task Contradictory_or_executable_observation_is_rejected(string mode, string? version,
        bool installed, bool healthy, string reason)
    {
        using var f = new Fixture(); f.Write(mode: mode, version: version, installed: installed, healthy: healthy, reason: reason);
        var advisory = (await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes.Single(x => x.Engine == "agno");
        Assert.False(advisory.Installed); Assert.False(advisory.ExecutionReady);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly WorkspaceScope Scope = new(Guid.NewGuid(), Guid.NewGuid());
        public readonly Guid Project = Guid.NewGuid();
        public readonly TimeProvider Clock = new FrozenClock();
        private readonly string folder = Directory.CreateTempSubdirectory("fanasa-advisory-tests-").FullName;
        public string Path => System.IO.Path.Combine(folder, "advisory.json");
        public Dictionary<string, string?> Settings() => new()
        {
            ["AgentRuntimeStatus:Bindings:test:OrganizationId"] = Scope.OrganizationId.ToString(),
            ["AgentRuntimeStatus:Bindings:test:WorkspaceId"] = Scope.WorkspaceId.ToString(),
            ["AgentRuntimeStatus:Bindings:test:ProjectId"] = Project.ToString(),
            ["AgentRuntimeStatus:Bindings:test:StatusFile"] = System.IO.Path.Combine(folder, "native-missing.json"),
            ["AgentRuntimeStatus:Bindings:test:AdvisoryStatusFile"] = Path
        };
        public FileAgentRuntimeStatus Reader(Dictionary<string, string?>? settings = null) =>
            new(new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build(), Clock);
        public void Write(int age = 0, string mode = "advisory-only", string? version = "3.1.3",
            bool installed = true, bool healthy = true, string reason = "advisory-healthy") =>
            File.WriteAllText(Path, JsonSerializer.Serialize(new { schema = "fanasa-agno-advisory/v1",
                observedAtUtc = Clock.GetUtcNow().AddSeconds(age), mode, version, installed, transportHealthy = healthy, reason }));
        public void Dispose() => Directory.Delete(folder, true);
    }
    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 14, 17, 0, 0, TimeSpan.Zero);
    }
}
