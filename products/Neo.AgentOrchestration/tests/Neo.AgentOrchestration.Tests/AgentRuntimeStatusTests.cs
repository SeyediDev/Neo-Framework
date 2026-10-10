using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class AgentRuntimeStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Fresh_installation_report_is_scoped_bounded_and_never_enables_execution()
    {
        using var f = new ReportFixture(); f.Write();
        var result = await f.Reader().ReadAsync(f.Scope, f.Project, Ct);
        Assert.Equal(f.Project, result.ProjectId); Assert.Equal(3, result.Runtimes.Count);
        Assert.All(result.Runtimes, x => { Assert.True(x.Installed); Assert.False(x.ExecutionReady); Assert.NotNull(x.ObservedAtUtc); });
        Assert.Equal("execution-gates-pending", result.Runtimes.Single(x => x.Engine == "hermes").Reason);
        Assert.Equal("cli-installed", result.Runtimes.Single(x => x.Engine == "codex").Reason);
        Assert.DoesNotContain(f.Path, JsonSerializer.Serialize(result));
        var foreign = await f.Reader().ReadAsync(new(Guid.NewGuid(), f.Scope.WorkspaceId), f.Project, Ct);
        Assert.All(foreign.Runtimes, x => Assert.Equal("not-configured", x.Reason));
        var other = await f.Reader().ReadAsync(f.Scope, Guid.NewGuid(), Ct);
        Assert.All(other.Runtimes, x => Assert.False(x.Installed));
    }
    [Theory]
    [InlineData(-181)] [InlineData(16)]
    public async Task Stale_or_future_observations_never_claim_current_health(int seconds)
    {
        using var f = new ReportFixture(); f.Write(seconds);
        Assert.All((await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes,
            x => { Assert.False(x.TransportHealthy); Assert.False(x.ExecutionReady); Assert.Equal("report-stale", x.Reason); });
    }
    [Theory]
    [InlineData("{\"schema\":\"fanasa-agent-runtime/v1\",\"token\":\"private\"}")]
    [InlineData("{\"schema\":\"one\",\"Schema\":\"two\"}")]
    [InlineData("[]")]
    [InlineData("{\"runtimes\":[null,null,null]}")]
    public async Task Malformed_or_secret_bearing_report_fails_closed(string body)
    {
        using var f = new ReportFixture(); File.WriteAllText(f.Path, body);
        var result = await f.Reader().ReadAsync(f.Scope, f.Project, Ct);
        Assert.All(result.Runtimes, x => { Assert.False(x.Installed); Assert.False(x.ExecutionReady); });
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
    }
    [Fact]
    public async Task Oversized_missing_and_ambiguous_reports_fail_closed()
    {
        using var f = new ReportFixture();
        Assert.All((await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes, x => Assert.False(x.Installed));
        File.WriteAllText(f.Path, new string('x', 32769));
        Assert.All((await f.Reader().ReadAsync(f.Scope, f.Project, Ct)).Runtimes, x => Assert.False(x.Installed));
        f.Write();
        var settings = f.Settings();
        foreach (var entry in f.Settings()) settings.Add(entry.Key.Replace(":test:", ":duplicate:"), entry.Value);
        var reader = new FileAgentRuntimeStatus(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), f.Clock);
        Assert.All((await reader.ReadAsync(f.Scope, f.Project, Ct)).Runtimes, x => Assert.Equal("not-configured", x.Reason));
    }
    [Fact]
    public async Task Http_read_requires_configure_membership_and_existing_project_before_runtime_access()
    {
        using var f = new WorkFixture(); var spy = new Spy();
        await using var api = new ApiFixture(f.Store, f.Clock, configure: s => s.AddSingleton<IAgentRuntimeStatus>(spy));
        var path = ApiFixture.Root(f.Scope) + $"/projects/{f.Project.Id}/agent-runtimes";
        using var anonymous = api.Client(f.Scope, invalid: "missing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path, Ct)).StatusCode);
        using var reader = api.Client(f.Scope, ["read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path, Ct)).StatusCode);
        using var foreign = api.Client(new(f.Scope.OrganizationId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync(path, Ct)).StatusCode);
        using var admin = api.Client(f.Scope);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(ApiFixture.Root(f.Scope) + $"/projects/{Guid.NewGuid()}/agent-runtimes", Ct)).StatusCode);
        Assert.Equal(0, spy.Calls);
        using var response = await admin.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(f.Project.Id, (await response.Content.ReadFromJsonAsync<AgentRuntimeCatalog>(Ct))!.ProjectId);
        Assert.Equal(1, spy.Calls);
    }
    private sealed class Spy : IAgentRuntimeStatus
    {
        public int Calls;
        public Task<AgentRuntimeCatalog> ReadAsync(WorkspaceScope scope, Guid projectId, CancellationToken ct)
        { Calls++; return Task.FromResult(new AgentRuntimeCatalog(projectId, [])); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Web_status_selector_is_scoped_read_only_and_works_with_spa_navigation(bool spa)
    {
        using var f = new WorkFixture(); await using var web = await WebFixture.Create(f);
        if (spa) web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation", "1");
        var path = web.Root + $"/agent-runtimes?ProjectId={f.Project.Id}";
        var html = await web.Html(path);
        Assert.Contains("اتصال ایجنت‌های VPS", WebUtility.HtmlDecode(html));
        Assert.Contains("Codex", html); Assert.Contains("OpenCode", html); Assert.Contains("Hermes", html);
        Assert.Contains("فعال نشده", WebUtility.HtmlDecode(html));
        Assert.Contains("برای این پروژه اتصالی پیکربندی نشده است", WebUtility.HtmlDecode(html));
        Assert.Contains("method=\"get\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(web.Token, html);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Client.GetAsync(web.Root + $"/agent-runtimes?ProjectId={Guid.NewGuid()}", Ct)).StatusCode);
        await using var reader = await WebFixture.Create(f, ["read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.GetAsync(path, Ct)).StatusCode);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    }
    private sealed class ReportFixture : IDisposable
    {
        public readonly WorkspaceScope Scope = new(Guid.NewGuid(), Guid.NewGuid());
        public readonly Guid Project = Guid.NewGuid();
        public readonly TimeProvider Clock = new Clock();
        private readonly string folder = Directory.CreateTempSubdirectory("fanasa-runtime-tests-").FullName;
        public string Path => System.IO.Path.Combine(folder, "status.json");
        public Dictionary<string, string?> Settings() => new()
        {
            ["AgentRuntimeStatus:Bindings:test:OrganizationId"] = Scope.OrganizationId.ToString(),
            ["AgentRuntimeStatus:Bindings:test:WorkspaceId"] = Scope.WorkspaceId.ToString(),
            ["AgentRuntimeStatus:Bindings:test:ProjectId"] = Project.ToString(),
            ["AgentRuntimeStatus:Bindings:test:StatusFile"] = Path
        };
        public FileAgentRuntimeStatus Reader() => new(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build(), Clock);
        public void Write(int ageSeconds = 0) => File.WriteAllText(Path, JsonSerializer.Serialize(new
        {
            schema = "fanasa-agent-runtime/v1", observedAtUtc = Clock.GetUtcNow().AddSeconds(ageSeconds),
            runtimes = new[] { new { engine = "hermes", version = "rev-5f045f842a60", installed = true, transportHealthy = true, reason = "native-healthy" },
                new { engine = "opencode", version = "1.18.35", installed = true, transportHealthy = true, reason = "native-healthy" },
                new { engine = "codex", version = "0.162.1", installed = true, transportHealthy = false, reason = "cli-installed" } }
        }));
        public void Dispose() => Directory.Delete(folder, true);
    }
}
