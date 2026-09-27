using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Api;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class HttpHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567"; // Isolated test evidence only.
    private static HarnessResult Success() => new("Succeeded", "Isolated gateway test completed.",
        [new("Commit", Sha, "NotApplicable"), new("Test", "gateway-fixture", "Passed", CommitSha: Sha)]);

    [Fact]
    public async Task Real_loopback_http_two_role_chain_accepts_callback_before_send_returns_and_preserves_evidence()
    {
        var f = await Fixture.Create();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var gateway = builder.Build(); HarnessFixture? fixture = null; var seen = new List<Guid>();
        gateway.MapPost("/runs", async (HttpContext context) =>
        {
            Assert.Equal("Bearer " + fixture!.Secrets.Dispatch, context.Request.Headers.Authorization.ToString());
            var request = (await context.Request.ReadFromJsonAsync<HarnessRequest>(Ct))!;
            Assert.Equal("neo-harness/v1", request.Protocol); Assert.Equal(request.RunId.ToString("N"), context.Request.Headers["Idempotency-Key"].ToString());
            Assert.Equal(f.Scope.WorkspaceId, request.WorkspaceId); Assert.NotEmpty(request.Work.Logs);
            Assert.Equal("http.demo", (await fixture.Read(request.RunId)).Run.Provider);
            seen.Add(request.RunId);
            // Would deadlock if the sender held the workspace SQL transaction.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await fixture.Callback(request.RunId, Success(), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return Results.Accepted();
        });
        await gateway.StartAsync(Ct);
        try
        {
            await using var h = await HarnessFixture.Create(f, gateway.Urls.Single() + "/runs"); fixture = h;
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var run = await h.Start(); Assert.Equal("Queued", run.Run.Status);
            var job = h.Job(http);
            Assert.Equal(6, await h.Drain(job));
            var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, h.Item.Id), Ct);
            Assert.Equal("Done", work.Item.Status); Assert.False(work.Item.IsTracking); Assert.Equal(4, work.Evidence.Count);
            Assert.Equal(2, seen.Distinct().Count());
            var runs = await h.Client.GetFromJsonAsync<AgentRunDetails[]>(h.Root + $"/items/{h.Item.Id}/runs", Ct);
            Assert.Equal("HandedOff", runs![0].Run.Decision); Assert.Equal("Completed", runs[1].Run.Decision);
            Assert.All(runs.SelectMany(x => x.Deliveries), d => Assert.Equal("Processed", d.State));
            var duplicate = await h.Callback(run.Run.Id, Success(), Ct); Assert.True((await duplicate.Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!.Duplicate);
            Assert.Equal(4, (await f.Handlers.Handle(new GetWorkItem(f.Scope, h.Item.Id), Ct)).Evidence.Count);
            var text = await h.Client.GetStringAsync(h.Root + $"/runs/{run.Run.Id}", Ct);
            Assert.DoesNotContain(h.Secrets.Dispatch, text); Assert.DoesNotContain(h.Secrets.Callback, text); Assert.DoesNotContain("harnessPayload", text);
        }
        finally { await gateway.StopAsync(Ct); }
    }

    [Fact]
    public async Task Callback_requires_connection_key_scope_and_matching_provider_and_rejects_conflicting_or_secret_results()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f); var run = await h.Start();
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted))));
        await h.Drain(h.Job(http), 1); // Snapshot is committed, HTTP not yet sent.
        var url = h.CallbackUrl(run.Run.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.PostAsJsonAsync(url, Success(), Ct)).StatusCode); // JWT alone is insufficient.
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Callback(run.Run.Id, Success(), Ct, key: h.Secrets.Dispatch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Callback(run.Run.Id, Success(), Ct, url: url.Replace(f.Scope.WorkspaceId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Callback(run.Run.Id, Success(), Ct, url: url.Replace("/harness/demo/", "/harness/other/"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Success() with { Summary = h.Secrets.Callback }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Success() with { Outcome = "99" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Success() with { Evidence = [new("Commit", "bad", "NotApplicable")] }, Ct)).StatusCode);
        Assert.Equal("AwaitingResult", (await h.Read(run.Run.Id)).Run.Status);
        var response = await h.Callback(run.Run.Id, Success(), Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!.Duplicate);
        Assert.True((await (await h.Callback(run.Run.Id, Success(), Ct)).Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!.Duplicate);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Callback(run.Run.Id, Success() with { Summary = "Changed result" }, Ct)).StatusCode);
        await using var db = f.Factory.CreateDbContext(); Assert.Equal(1, await db.InboxReceipts.CountAsync(x => x.WorkItemId == h.Item.Id, Ct));
        using var callbackOnly = h.Api.Factory.CreateClient(); callbackOnly.DefaultRequestHeaders.Add(HarnessAuthentication.Header, h.Secrets.Callback);
        Assert.Equal(HttpStatusCode.Unauthorized, (await callbackOnly.GetAsync(h.Root + "/items", Ct)).StatusCode);
        using var schema = JsonDocument.Parse(await h.Client.GetStringAsync("/swagger/v1/swagger.json", Ct));
        var path = "/api/orchestration/v1/organizations/{organizationId}/workspaces/{workspaceId}/harness/{connection}/runs/{runId}/result";
        var security = schema.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post").GetProperty("security");
        var requirement = Assert.Single(security.EnumerateArray());
        Assert.Equal(HarnessAuthentication.Name, Assert.Single(requirement.EnumerateObject()).Name);
    }

    [Fact]
    public async Task Repeated_transport_failure_uses_stable_payload_and_key_and_stops_at_neos_retry_limit_without_secret_leaks()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f); var run = await h.Start();
        var bodies = new List<string>(); var keys = new List<string>();
        using var http = new HttpClient(new DelegateHandler(async (request, token) =>
        { bodies.Add(await request.Content!.ReadAsStringAsync(token)); keys.Add(request.Headers.GetValues("Idempotency-Key").Single()); throw new HttpRequestException(h.Secrets.Dispatch); }));
        var job = h.Job(http); await h.Drain(job, 1); var send = await h.Next();
        for (var i = 0; i < 3; i++)
        {
            if (i == 0) await h.Queue(send);
            else
            {
                await using var db = f.Factory.CreateDbContext();
                await db.OutboxMessages.Where(x => x.Id == send).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)), Ct);
            }
            var error = await Assert.ThrowsAsync<DeliveryProcessingException>(() => job.Execute(send, Ct));
            Assert.DoesNotContain(h.Secrets.Dispatch, error.ToString());
        }
        Assert.Equal(3, bodies.Count); Assert.Single(bodies.Distinct()); Assert.Single(keys.Distinct());
        await job.Execute(send, Ct); Assert.Equal(3, bodies.Count);
        var saved = await h.Read(run.Run.Id); Assert.Equal("AwaitingResult", saved.Run.Status);
        var delivery = saved.Deliveries.Single(x => x.Kind == "SendHarnessRequest"); Assert.Equal("Failed", delivery.State);
        Assert.DoesNotContain(h.Secrets.Dispatch, delivery.Error!); Assert.Equal(3, delivery.ExecutionAttempts);
        // A late, authenticated callback still resolves the uncertain outcome.
        Assert.Equal(HttpStatusCode.OK, (await h.Callback(run.Run.Id, Success(), Ct)).StatusCode);
    }

    [Fact]
    public async Task Permanent_response_waits_for_reconciliation_without_redirect_or_false_completion()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f); var run = await h.Start(); var calls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)); }));
        var job = h.Job(http); await h.Drain(job);
        Assert.Equal(1, calls); var saved = await h.Read(run.Run.Id);
        Assert.Equal("AwaitingResult", saved.Run.Status); Assert.Equal("Waiting", saved.Run.Decision);
        Assert.Equal("harness-response-requires-reconciliation", saved.Run.DecisionReason);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync(h.Root + $"/runs/{run.Run.Id}/return-assignment",
            new ReturnRunAssignmentRequest(saved.Run.WorkItemVersion), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NeedsInput")]
    public async Task Negative_result_closes_time_without_automatic_handoff(string outcome)
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f); var run = await h.Start();
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted))));
        await h.Drain(h.Job(http)); Assert.Equal(HttpStatusCode.OK, (await h.Callback(run.Run.Id, new(outcome, "Gateway requires operator input."), Ct)).StatusCode);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, h.Item.Id), Ct);
        Assert.Equal("Blocked", work.Item.Status); Assert.False(work.Item.IsTracking); Assert.Empty(work.Evidence);
        Assert.Null((await h.Read(run.Run.Id)).Run.NextRunId);
    }
    [Fact]
    public async Task Changed_destination_does_not_send_a_prepared_snapshot_to_a_new_service()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f); var run = await h.Start(); var calls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)); }));
        var job = h.Job(http); await h.Drain(job, 1);
        h.Api.Factory.Services.GetRequiredService<IConfiguration>()["Harness:Connections:demo:Endpoint"] = "https://replacement.example.test/runs";
        await h.Drain(job);
        Assert.Equal(0, calls); var saved = await h.Read(run.Run.Id);
        Assert.Equal("AwaitingResult", saved.Run.Status); Assert.Equal("harness-configuration-changed-reconcile-before-replay", saved.Run.DecisionReason);
    }

    [Fact]
    public async Task External_execution_requires_explicit_consent_and_enabled_matching_scope_configuration()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync(h.Root + $"/items/{h.Item.Id}/runs", h.StartBody with { AllowExternalExecution = false }, Ct)).StatusCode);
        var settings = new Dictionary<string,string?>(h.Settings) { ["Harness:Enabled"] = "false" };
        var policy = new ConfiguredRunProviders(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new TestEnvironment());
        Assert.Throws<RunProviderUnavailableException>(() => policy.RequireAvailable("http.demo", f.Scope));
        settings["Harness:Enabled"] = "true"; settings["Harness:Connections:demo:Endpoint"] = "http://remote.example/runs";
        policy = new ConfiguredRunProviders(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new TestEnvironment());
        Assert.Throws<RunProviderUnavailableException>(() => policy.RequireAvailable("http.demo", f.Scope));
        await using var db = f.Factory.CreateDbContext(); Assert.False(await db.AgentRuns.AnyAsync(x => x.WorkItemId == h.Item.Id, Ct));
    }

    internal sealed class HarnessFixture : IAsyncDisposable
    {
        public required Fixture Sql { get; init; }
        public required ApiFixture Api { get; init; }
        public required HttpClient Client { get; init; }
        public required WorkItemView Item { get; init; }
        public required StartAgentRunRequest StartBody { get; init; }
        public required Dictionary<string,string?> Settings { get; init; }
        public required TestSecrets Secrets { get; init; }
        public ConfiguredRunProviders Providers => Api.Factory.Services.GetRequiredService<ConfiguredRunProviders>();
        public string Root => ApiFixture.Root(Sql.Scope);
        public static async Task<HarnessFixture> Create(Fixture f, string endpoint = "https://gateway.example.test/runs")
        {
            var setup = await SqlRunTests.Configure(f, evidence: true);
            await using (var db = f.Factory.CreateDbContext())
            {
                foreach (var agent in await db.Agents.Where(x => x.WorkspaceId == f.Scope.WorkspaceId).ToArrayAsync(Ct))
                    agent.Update(f.Scope, agent.Name, "http.demo", "test-model", "Test gateway instructions", null);
                await db.SaveChangesAsync(Ct);
            }
            var item = await SqlRunTests.Ready(f); var secrets = new TestSecrets();
            var settings = new Dictionary<string,string?> { ["Harness:Enabled"] = "true", ["Harness:AllowLoopbackHttp"] = "true" };
            foreach (var key in new[] { "demo", "other" })
            {
                var prefix = "Harness:Connections:" + key + ":";
                settings[prefix + "Enabled"] = "true"; settings[prefix + "OrganizationId"] = f.Scope.OrganizationId.ToString();
                settings[prefix + "WorkspaceId"] = f.Scope.WorkspaceId.ToString(); settings[prefix + "Endpoint"] = endpoint;
                settings[prefix + "CallbackBaseUrl"] = "https://api.example.test/";
                settings[prefix + "DispatchSecretRef"] = "env:NEO_TEST_DISPATCH"; settings[prefix + "CallbackSecretRef"] = "env:NEO_TEST_CALLBACK";
            }
            var api = new ApiFixture(clock: f.Clock, sql: f.Connection, settings: settings,
                configure: s => s.AddSingleton<IHarnessSecrets>(secrets));
            return new() { Sql = f, Api = api, Client = api.Client(f.Scope, ["read", "write", "execute"]), Item = item,
                StartBody = new(Guid.NewGuid(), item.Version, setup.Flow.Id, setup.Flow.Version, f.Role.Id, AllowExternalExecution: true),
                Settings = settings, Secrets = secrets };
        }
        public Task<AgentRunDetails> Start() => ApiFixture.Post<AgentRunDetails>(Client, Root + $"/items/{Item.Id}/runs", StartBody, HttpStatusCode.Accepted);
        public async Task<AgentRunDetails> Read(Guid run) => (await Client.GetFromJsonAsync<AgentRunDetails>(Root + $"/runs/{run}", Ct))!;
        public string CallbackUrl(Guid run) => Root + $"/harness/demo/runs/{run}/result";
        public async Task<HttpResponseMessage> Callback(Guid run, HarnessResult result, CancellationToken ct, string? key = null, string? url = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url ?? CallbackUrl(run)) { Content = JsonContent.Create(result) };
            request.Headers.Add(HarnessAuthentication.Header, key ?? Secrets.Callback);
            return await Client.SendAsync(request, ct);
        }
        public RunOutboxJob Job(HttpClient http) => new(new(Sql.Factory), new(new FakeHarness(), Sql.Clock, Providers),
            new(http, Sql.Store, Providers, Secrets), Sql.Clock);
        public async Task<long> Next()
        {
            await using var db = Sql.Factory.CreateDbContext();
            return await db.Deliveries.Where(x => x.WorkItemId == Item.Id && x.Outbox.OutboxState == OutboxState.Requested)
                .OrderBy(x => x.OutboxId).Select(x => x.OutboxId).FirstOrDefaultAsync(Ct);
        }
        public async Task Queue(long id)
        {
            await using var db = Sql.Factory.CreateDbContext(); var engine = new EfOutboxStore<OrchestrationDbContext>(db); var lease = Guid.NewGuid();
            Assert.NotNull(await engine.ClaimDispatchAsync(id, lease, TimeSpan.FromMinutes(5), Ct));
            await engine.CompleteDispatchAsync(id, lease, "http-test", null, Ct);
        }
        public async Task<int> Drain(RunOutboxJob job, int limit = 30)
        {
            var count = 0;
            while (count < limit && await Next() is var id && id != 0)
            { await Queue(id); Sql.Clock.Advance(1); await job.Execute(id, Ct); await job.Execute(id, Ct); count++; }
            return count;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await Api.DisposeAsync(); }
    }
    internal sealed class TestSecrets : IHarnessSecrets
    {
        public string Dispatch { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Callback { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Resolve(string reference) => reference switch
        { "env:NEO_TEST_DISPATCH" => Dispatch, "env:NEO_TEST_CALLBACK" => Callback, _ => throw new InvalidOperationException("Unknown test secret.") };
    }
    private sealed class DelegateHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
