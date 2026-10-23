using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;
using HarnessFixture = Neo.AgentOrchestration.Tests.HttpHarnessTests.HarnessFixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class HarnessUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HarnessUsage Report = new("codex:turn-1", "model-route", "test-model", 100, 20, 80, 5);
    private static HarnessResult Result(string outcome = "Succeeded") => new(outcome, "Isolated fixture, not a model result.",
        [new("Test", "fixture", "Passed")], [Report, Report with { ReportId = "codex:turn-2", InputTokens = null, OutputTokens = 0, CachedInputTokens = null, ReasoningTokens = null }]);

    [Fact]
    public void Null_usage_preserves_legacy_result_bytes_and_fingerprint_opt_in_fences_dispatch()
    {
        var body = new HarnessResult("Succeeded", "Result", []);
        Assert.Equal("{\"outcome\":\"Succeeded\",\"summary\":\"Result\",\"evidence\":[]}", JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var connection = new Neo.AgentOrchestration.Infrastructure.Runs.HttpHarnessConnection("demo", new("https://gateway.invalid/runs"), new("https://api.invalid/"), "env:DISPATCH", "env:CALLBACK", false);
        Assert.NotEqual(connection.Fingerprint, (connection with { UsageReportsOptIn = true }).Fingerprint);
        Assert.NotEqual((connection with { CompactContextOptIn = true }).Fingerprint,
            (connection with { CompactContextOptIn = true, UsageReportsOptIn = true }).Fingerprint);
    }

    [Theory]
    [InlineData("Succeeded", "Review")]
    [InlineData("Failed", "Blocked")]
    [InlineData("NeedsInput", "Blocked")]
    public async Task Authenticated_usage_commits_with_result_once_and_keeps_nullable_coverage(string outcome, string state)
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f, usageReports: true);
        var run = await h.Start(); using var http = Accepted(); await h.Drain(h.Job(http));
        var result = Result(outcome);
        var first = await h.Callback(run.Run.Id, result, Ct); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!;
        var saved = await h.Read(run.Run.Id); Assert.Equal(2, saved.TokenUsage!.Count);
        Assert.All(saved.TokenUsage, x => { Assert.Equal("http.demo", x.Provider); Assert.Equal("reported-harness", x.Source); Assert.Equal(run.Run.Id, x.AgentRunId); });
        var partial = Assert.Single(saved.TokenUsage, x => x.InputTokens is null);
        Assert.Equal(0, partial.OutputTokens); Assert.Null(partial.CachedInputTokens); Assert.Null(partial.ReasoningTokens);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, h.Item.Id), Ct);
        Assert.Equal(state, work.Item.Status); Assert.False(work.Item.IsTracking);
        Assert.Null(work.Item.TokenMeter!.TotalTokens); Assert.Equal(120, work.Item.TokenMeter.KnownTotalTokens);
        Assert.Equal(2, work.Item.TokenMeter.ReportCount);
        var repeat = (await (await h.Callback(run.Run.Id, result, Ct)).Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!;
        Assert.True(repeat.Duplicate); Assert.Equal(receipt.ReceiptId, repeat.ReceiptId);
        Assert.Equal(saved.TokenUsage.Select(x => x.Id), (await h.Read(run.Run.Id)).TokenUsage!.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await h.Callback(run.Run.Id, result with { Usage = [Report with { OutputTokens = 21 }] }, Ct)).StatusCode);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(2, await db.TokenUsageReports.CountAsync(x => x.AgentRunId == run.Run.Id, Ct));
        Assert.Equal(1, await db.InboxReceipts.CountAsync(x => x.WorkItemId == h.Item.Id, Ct));
    }

    [Fact]
    public async Task Usage_requires_explicit_connection_opt_in_and_directional_authentication()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f);
        var run = await h.Start(); using var http = Accepted(); await h.Drain(h.Job(http));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Result(), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Callback(run.Run.Id, Result(), Ct, key: h.Secrets.Dispatch)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Callback(run.Run.Id, Result() with { Usage = null }, Ct, url: h.CallbackUrl(run.Run.Id).Replace("/harness/demo/", "/harness/other/"))).StatusCode);
        Assert.Equal("AwaitingResult", (await h.Read(run.Run.Id)).Run.Status); Assert.Empty((await h.Read(run.Run.Id)).TokenUsage!);
        Assert.Equal(HttpStatusCode.OK, (await h.Callback(run.Run.Id, Result() with { Usage = null }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Changed_connection_cannot_inject_usage_into_an_older_frozen_run()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f, usageReports: true);
        var run = await h.Start(); using var http = Accepted(); await h.Drain(h.Job(http));
        h.Api.Factory.Services.GetRequiredService<IConfiguration>()["Harness:Connections:demo:Endpoint"] = "https://replacement.invalid/runs";
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Callback(run.Run.Id, Result(), Ct)).StatusCode);
        Assert.Equal("AwaitingResult", (await h.Read(run.Run.Id)).Run.Status);
        Assert.Empty((await h.Read(run.Run.Id)).TokenUsage!);
    }

    [Fact]
    public async Task Invalid_usage_and_failed_evidence_roll_back_all_effects()
    {
        var f = await Fixture.Create(); await using var h = await HarnessFixture.Create(f, usageReports: true);
        var run = await h.Start(); using var http = Accepted(); await h.Drain(h.Job(http));
        IReadOnlyList<HarnessUsage>[] bad = [
            [Report, Report], [Report with { InputTokens = -1 }], [Report with { CachedInputTokens = 101 }],
            [Report with { ReasoningTokens = 21 }], [Report with { InputTokens = long.MaxValue }],
            [Report with { Provider = h.Secrets.Callback }], [Report with { ReportId = "bad\nidentity" }],
            [Report with { Model = new string('x', 201) }], Enumerable.Range(0, 101).Select(i => Report with { ReportId = i.ToString() }).ToArray()];
        foreach (var reports in bad)
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Result() with { Usage = reports }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Callback(run.Run.Id, Result() with { Evidence = [new("Commit", "not-a-sha", "NotApplicable")] }, Ct)).StatusCode);
        Assert.Equal("AwaitingResult", (await h.Read(run.Run.Id)).Run.Status); Assert.Empty((await h.Read(run.Run.Id)).TokenUsage!);
        await using var db = f.Factory.CreateDbContext();
        Assert.False(await db.InboxReceipts.AnyAsync(x => x.WorkItemId == h.Item.Id, Ct));
        var item = await f.Handlers.Handle(new GetWorkItem(f.Scope, h.Item.Id), Ct);
        Assert.Equal("InProgress", item.Item.Status); Assert.True(item.Item.IsTracking); Assert.Empty(item.Evidence);
    }

    private static HttpClient Accepted() => new(new AcceptHandler());
    private sealed class AcceptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }
}
