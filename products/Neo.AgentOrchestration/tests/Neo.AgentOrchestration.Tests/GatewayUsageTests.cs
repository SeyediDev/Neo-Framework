using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class SqlGatewayJournalTests
{
    [Fact]
    public async Task Total_usage_limit_is_atomic_across_observations_not_only_per_poll()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var lease = Guid.NewGuid(); await f.Journal.AcquireAsync(f.Scope, lease, f.Clock.GetUtcNow(), Ct);
        var reports = Enumerable.Range(0, 100).Select(i => new ExternalAgentUsage("turn:" + i.ToString("D3"), "native", "test", 1, null, null, null)).ToArray();
        await f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(), reports, Ct);
        await f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(), [reports[0]], Ct);
        var error = await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(),
            [reports[0], reports[0] with { ReportId = "turn:new" }], Ct));
        Assert.Equal("gateway-usage-limit", error.Code);
        var saved = await f.Journal.ReadUsageAsync(f.Scope, Ct);
        Assert.Equal(100, saved.Count); Assert.Equal(reports.Select(x => x.ReportId), saved.Select(x => x.ReportId));
    }

    [Fact]
    public async Task Managed_dispatch_to_journal_to_authenticated_callback_records_usage_once_and_stops_at_Review()
    {
        var product = await SqlPersistenceTests.Fixture.Create();
        await using var harness = await HttpHarnessTests.HarnessFixture.Create(product, usageReports: true);
        var managed = await harness.Start();
        var f = await Fixture.Create(new(product.Scope.OrganizationId, product.Scope.WorkspaceId, product.Project.Id, managed.Run.Id));
        f.Bindings.Profile = managed.Run.AgentProfileId; f.Bindings.UsageReportsOptIn = true;
        using var sender = new HttpClient(new DispatchToJournal(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync(Ct);
            Assert.Equal(managed.Run.Id.ToString("N"), request.Headers.GetValues("Idempotency-Key").Single());
            return await f.Execution().ReserveAsync("demo", body, managed.Run.Id, Ct);
        }));
        await harness.Drain(harness.Job(sender));
        Assert.Equal("AwaitingResult", (await harness.Read(managed.Run.Id)).Run.Status);
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        f.Adapter.Usage = [new("fixture-turn", "native-route", "test-model", 30, 7, 20, null)];
        f.Adapter.State = ExternalAgentState.Completed; f.Sandbox.Exited = true;
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        var lost = true; Guid? receipt = null;
        f.Delivery.Sender = async body =>
        {
            var response = await harness.Callback(managed.Run.Id, JsonSerializer.Deserialize<HarnessResult>(body, Json)!, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<HarnessReceipt>(Ct))!;
            if (lost) { lost = false; receipt = result.ReceiptId; Assert.False(result.Duplicate); throw new ExternalAgentException("test-receipt-lost", true); }
            Assert.True(result.Duplicate); Assert.Equal(receipt, result.ReceiptId); return result.ReceiptId;
        };
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.CallbackReady, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.Delivered, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        var saved = await harness.Read(managed.Run.Id); Assert.Equal("Succeeded", saved.Run.Status);
        Assert.Equal("Pending", saved.Run.Decision); Assert.Null(saved.Run.NextRunId);
        var usage = Assert.Single(saved.TokenUsage!); Assert.Equal(30, usage.InputTokens); Assert.Equal(7, usage.OutputTokens);
        var item = await product.Handlers.Handle(new GetWorkItem(product.Scope, harness.Item.Id), Ct);
        Assert.Equal("Review", item.Item.Status); Assert.False(item.Item.IsTracking); Assert.Equal(37, item.Item.TokenMeter!.TotalTokens);
        Assert.Single(item.Evidence); Assert.Equal(1, f.Adapter.Submitted); Assert.Equal(f.Delivery.Bodies[0], f.Delivery.Bodies[1]);
        await using var db = product.Factory.CreateDbContext();
        Assert.Equal(1, await db.TokenUsageReports.CountAsync(x => x.AgentRunId == managed.Run.Id, Ct));
        Assert.False(await db.Set<Neo.AgentOrchestration.Domain.Work.WorkTokenUsage>().AnyAsync(x => x.WorkItemId == harness.Item.Id, Ct));
    }

    private sealed class DispatchToJournal(Func<HttpRequestMessage, Task<GatewayRun>> reserve) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await reserve(request); return new(HttpStatusCode.Accepted); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Journaled_usage_is_frozen_in_opted_in_result_and_exactly_retried(bool optedIn, bool failed)
    {
        var f = await Fixture.Create(); f.Bindings.UsageReportsOptIn = optedIn;
        await f.Execution().ReserveAsync("coding", f.Body(), Ct); await f.Execution().AdvanceAsync(f.Scope, Ct);
        var report = new ExternalAgentUsage("turn:earlier", "native-provider", "test-model", 10, 5, null, null);
        f.Adapter.Usage = [report]; f.Adapter.State = ExternalAgentState.Running;
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        f.Adapter.Usage = [report with { ReportId = "turn:last", InputTokens = null, OutputTokens = 0 }];
        f.Adapter.State = failed ? ExternalAgentState.Failed : ExternalAgentState.Completed;
        f.Sandbox.Exited = true; f.Sandbox.Outcome = failed ? "Failed" : "Succeeded";
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        var ready = await f.Journal.ReadAsync(f.Scope, Ct); Assert.Equal(GatewayPhase.CallbackReady, ready.Phase);
        var result = JsonSerializer.Deserialize<HarnessResult>(ready.FrozenResult!, Json)!;
        if (optedIn)
        {
            Assert.Equal(2, result.Usage!.Count);
            Assert.Equal("turn:earlier", result.Usage[0].ReportId); Assert.Equal("turn:last", result.Usage[1].ReportId);
            Assert.Null(result.Usage[1].InputTokens); Assert.Equal(0, result.Usage[1].OutputTokens);
        }
        else { Assert.Null(result.Usage); Assert.DoesNotContain("\"usage\"", ready.FrozenResult!); }
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.ReadUsageAsync(f.Scope with { ProjectId = Guid.NewGuid() }, Ct));
        f.Delivery.LoseNext = true; await f.Execution().AdvanceAsync(f.Scope, Ct);
        // Observation changes after freeze cannot rewrite delivered counters.
        f.Adapter.Usage = [report with { InputTokens = 999 }];
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(2, f.Delivery.Bodies.Count); Assert.All(f.Delivery.Bodies, x => Assert.Equal(ready.FrozenResult, x));
        Assert.Equal(GatewayPhase.Delivered, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
    }
}
