using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class SqlRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http_create_dispatch_callback_handoff_done_survives_fresh_contexts_and_duplicate_jobs()
    {
        var f = await Fixture.Create(); var setup = await Configure(f);
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection, simulation: true);
        using var client = api.Client(f.Scope, ["read", "write", "execute"]);
        var root = ApiFixture.Root(f.Scope);
        var item = await ApiFixture.Post<WorkItemDetails>(client, root + "/items",
            new CreateWorkItemRequest(f.Project.Id, "e2e", "Run E2E", "orchestration"), HttpStatusCode.Created);
        item = await ApiFixture.Post<WorkItemDetails>(client, root + $"/items/{item.Item.Id}/status", new ChangeStatusRequest(item.Item.Version, "Ready"));
        var start = new StartAgentRunRequest(Guid.NewGuid(), item.Item.Version, setup.Flow.Id, setup.Flow.Version, f.Role.Id);
        var url = root + $"/items/{item.Item.Id}/runs";
        var started = await ApiFixture.Post<AgentRunDetails>(client, url, start, HttpStatusCode.Accepted);
        var duplicate = await ApiFixture.Post<AgentRunDetails>(client, url, start, HttpStatusCode.Accepted);
        Assert.Equal(started.Run.Id, duplicate.Run.Id); Assert.Single(started.Deliveries);
        Assert.Equal("Queued", started.Run.Status); Assert.Equal("Requested", started.Deliveries[0].State);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, start with { SimulationOutcome = "Failed" }, Ct)).StatusCode);
        using var stranger = api.Client(f.Scope, ["read", "execute"], subject: "other-requestor");
        Assert.Equal(HttpStatusCode.Conflict, (await stranger.PostAsJsonAsync(url, start, Ct)).StatusCode);
        var deliveries = await Drain(f, item.Item.Id);
        Assert.Equal(6, deliveries);
        var finished = (await client.GetFromJsonAsync<WorkItemDetails>(root + $"/items/{item.Item.Id}", Ct))!;
        var runs = (await client.GetFromJsonAsync<AgentRunDetails[]>(url, Ct))!;
        Assert.Equal("Done", finished.Item.Status); Assert.False(finished.Item.IsTracking);
        Assert.Equal(2, runs.Length); Assert.Equal("HandedOff", runs[0].Run.Decision); Assert.Equal("Completed", runs[1].Run.Decision);
        Assert.Equal(runs[0].Run.Id, runs[1].Run.PreviousRunId); Assert.Equal(runs[1].Run.Id, runs[0].Run.NextRunId);
        Assert.Equal(2, finished.TimeEntries.Count); Assert.All(finished.TimeEntries, x => Assert.NotNull(x.EndedAtUtc));
        Assert.Empty(finished.Evidence); // Fake execution never invents commits/tests.
        Assert.Equal(4, finished.Item.ElapsedSeconds);
        Assert.All(runs.SelectMany(x => x.Deliveries), x => Assert.Equal("Processed", x.State));
        Assert.All(runs, x => Assert.Contains("simulation", x.Run.ResultSummary!, StringComparison.OrdinalIgnoreCase));
        var saved = await ApiFixture.Post<AgentRunDetails>(client, url, start, HttpStatusCode.Accepted);
        Assert.Equal(started.Run.Id, saved.Run.Id); Assert.Equal("HandedOff", saved.Run.Decision);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(2, await db.InboxReceipts.CountAsync(x => x.WorkItemId == item.Item.Id, Ct));
        Assert.Equal(2, await db.AgentRuns.CountAsync(x => x.WorkItemId == item.Item.Id, Ct));
        // A completed managed item can be handed back and archived through the
        // ordinary HTTP lifecycle, without granting synthetic-owner identities.
        var returned = await ApiFixture.Post<AgentRunDetails>(client, root + $"/runs/{runs[1].Run.Id}/return-assignment",
            new ReturnRunAssignmentRequest(finished.Item.Version));
        Assert.Equal("assignment-returned-to-requestor", returned.Run.DecisionReason);
        finished = (await client.GetFromJsonAsync<WorkItemDetails>(root + $"/items/{item.Item.Id}", Ct))!;
        Assert.Equal("agent-a", finished.Item.OwnerAgentId);
        var archived = await ApiFixture.Post<WorkItemDetails>(client, root + $"/items/{item.Item.Id}/archive", new VersionRequest(finished.Item.Version));
        Assert.True(archived.Item.IsArchived);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NeedsInput")]
    public async Task Negative_callback_blocks_item_closes_timer_and_does_not_claim_success(string outcome)
    {
        var f = await Fixture.Create(); var setup = await Configure(f); var item = await Ready(f);
        var run = await Start(f, setup, item, outcome);
        Assert.Equal(2, await Drain(f, item.Id));
        var saved = await Runs(f).Handle(new GetAgentRun(f.Scope, run.Run.Id), Ct);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct);
        Assert.Equal(outcome, saved.Run.Status); Assert.Equal("NotApplicable", saved.Run.Decision);
        Assert.Equal("Blocked", work.Item.Status); Assert.False(work.Item.IsTracking); Assert.Empty(work.Evidence);
        Assert.Null(saved.Run.NextRunId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Runs(f).Handle(new ReturnRunAssignment(f.Scope, saved.Run.Id,
            new("stranger", "other-chat"), new(work.Item.Version)), Ct));
        await Runs(f).Handle(new ReturnRunAssignment(f.Scope, saved.Run.Id, f.Actor, new(work.Item.Version)), Ct);
        await f.Change(item.Id, new StatusChange(WorkItemStatus.Ready));
        Assert.Equal("Ready", (await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct)).Item.Status);
    }

    [Fact]
    public async Task Approval_gate_waits_and_rejects_requestor_self_approval_before_explicit_reevaluation()
    {
        var f = await Fixture.Create(); var setup = await Configure(f, approval: true); var item = await Ready(f);
        var first = await Start(f, setup, item); Assert.Equal(3, await Drain(f, item.Id));
        var run = await Runs(f).Handle(new GetAgentRun(f.Scope, first.Run.Id), Ct);
        Assert.Equal("Waiting", run.Run.Decision); Assert.Contains("approval-required", run.Run.DecisionReason!);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct);
        var body = new ApproveWorkflowRequest(setup.Flow.Version, item.Id, work.Item.Version,
            setup.Flow.Transitions.Single(x => x.FromRoleId == f.Role.Id).Id, true, "Reviewed");
        var workspace = new WorkspaceHandlers(f.Store, f.Clock);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => workspace.Handle(new ApproveWorkflow(f.Scope, setup.Flow.Id, f.Actor, body), Ct));
        await workspace.Handle(new ApproveWorkflow(f.Scope, setup.Flow.Id, new("reviewer", "review-chat"), body), Ct);
        var evaluate = new EvaluateAgentRun(f.Scope, run.Run.Id, f.Actor, new(Guid.NewGuid(), work.Item.Version, setup.Flow.Version));
        await Runs(f).Handle(evaluate, Ct); await Runs(f).Handle(evaluate, Ct);
        Assert.Equal(4, await Drain(f, item.Id));
        Assert.Equal("Done", (await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct)).Item.Status);
    }

    [Fact]
    public async Task Missing_commit_and_test_gates_remain_waiting_without_fabricated_evidence()
    {
        var f = await Fixture.Create(); var setup = await Configure(f, evidence: true); var item = await Ready(f);
        var first = await Start(f, setup, item); await Drain(f, item.Id);
        var run = await Runs(f).Handle(new GetAgentRun(f.Scope, first.Run.Id), Ct);
        Assert.Equal("Waiting", run.Run.Decision); Assert.NotNull(run.Run.DecisionReason);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct);
        Assert.Equal("Review", work.Item.Status); Assert.Empty(work.Evidence); Assert.False(work.Item.IsTracking);
    }

    [Fact]
    public async Task Disabled_profile_before_dispatch_records_needs_input_without_callback()
    {
        var f = await Fixture.Create(); var setup = await Configure(f); var item = await Ready(f);
        var first = await Start(f, setup, item);
        await using (var db = f.Factory.CreateDbContext())
        { (await db.Agents.SingleAsync(x => x.Id == setup.Agent.Id, Ct)).SetEnabled(f.Scope, false); await db.SaveChangesAsync(Ct); }
        Assert.Equal(1, await Drain(f, item.Id));
        var run = await Runs(f).Handle(new GetAgentRun(f.Scope, first.Run.Id), Ct);
        Assert.Equal("NeedsInput", run.Run.Status); Assert.Null(run.Run.DispatchedAtUtc);
        Assert.Equal("Blocked", (await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct)).Item.Status);
    }

    [Fact]
    public async Task Changed_workflow_waits_until_explicit_versioned_reevaluation()
    {
        var f = await Fixture.Create(); var setup = await Configure(f); var item = await Ready(f);
        var first = await Start(f, setup, item); await Drain(f, item.Id, limit: 2);
        Guid newVersion;
        await using (var db = f.Factory.CreateDbContext())
        {
            var flow = await db.Workflows.SingleAsync(x => x.Id == setup.Flow.Id, Ct);
            flow.Update(f.Scope, "Updated", true); newVersion = flow.Version; await db.SaveChangesAsync(Ct);
        }
        await Drain(f, item.Id);
        var run = await Runs(f).Handle(new GetAgentRun(f.Scope, first.Run.Id), Ct);
        Assert.Equal("workflow-version-changed", run.Run.DecisionReason);
        var work = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => Runs(f).Handle(new EvaluateAgentRun(f.Scope, run.Run.Id,
            f.Actor, new(Guid.NewGuid(), work.Item.Version, setup.Flow.Version)), Ct));
        await Runs(f).Handle(new EvaluateAgentRun(f.Scope, run.Run.Id, f.Actor, new(Guid.NewGuid(), work.Item.Version, newVersion)), Ct);
        await Drain(f, item.Id);
        Assert.Equal("Done", (await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct)).Item.Status);
    }

    [Fact]
    public async Task Run_endpoints_require_execute_scope_opt_in_and_cannot_impersonate_a_managed_owner()
    {
        var f = await Fixture.Create(); var setup = await Configure(f); var item = await Ready(f);
        var body = new StartAgentRunRequest(Guid.NewGuid(), item.Version, setup.Flow.Id, setup.Flow.Version, f.Role.Id);
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection);
        var root = ApiFixture.Root(f.Scope); var url = root + $"/items/{item.Id}/runs";
        using var writer = api.Client(f.Scope, ["read", "write"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync(url, body, Ct)).StatusCode);
        using var execute = api.Client(f.Scope, ["read", "execute"]);
        var disabled = await execute.PostAsJsonAsync(url, body, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Contains("simulation-disabled", await disabled.Content.ReadAsStringAsync(Ct));
        using var forged = api.Client(f.Scope, ["read", "write", "execute"], "neo-run:" + body.RequestId.ToString("N"));
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.GetAsync(root + $"/items/{item.Id}", Ct)).StatusCode);
        await using var db = f.Factory.CreateDbContext(); Assert.False(await db.AgentRuns.AnyAsync(x => x.WorkItemId == item.Id, Ct));
        var other = await Fixture.Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Runs(f).Handle(new GetAgentRuns(other.Scope, item.Id), Ct));
    }

    [Fact]
    public async Task Concurrent_start_requests_cannot_take_a_busy_role_or_overwrite_a_claim()
    {
        var f = await Fixture.Create(); var setup = await Configure(f); var a = await Ready(f, "a"); var b = await Ready(f, "b");
        async Task<bool> TryStart(WorkItemView item)
        { try { await Start(f, setup, item); return true; } catch (WorkItemConflictException) { return false; } }
        var results = await Task.WhenAll(TryStart(a), TryStart(b)); Assert.Single(results, x => x);
        await using var db = f.Factory.CreateDbContext(); Assert.Equal(1, await db.AgentRuns.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
    }

    internal static RunHandlers Runs(Fixture f) => new(f.Store, new SqlDurableWorkStore(f.Factory, f.Clock), f.Clock);
    internal static async Task<(WorkflowDefinition Flow, AgentProfile Agent)> Configure(Fixture f, bool approval = false, bool evidence = false)
    {
        await using var db = f.Factory.CreateDbContext();
        var workspace = await db.Workspaces.SingleAsync(x => x.Id == f.Scope.WorkspaceId, Ct);
        var reviewer = RoleProfile.Create(workspace, "reviewer", "Reviewer");
        var agent = AgentProfile.Create(f.Scope, f.Role, "developer", "Developer", "fake", instructions: "Simulate development only.");
        var review = AgentProfile.Create(f.Scope, reviewer, "reviewer", "Reviewer", "fake");
        var flow = WorkflowDefinition.Create(f.Scope, f.Project, "main", "Main");
        flow.ConfigureTransition(f.Scope, "review", f.Role, WorkItemStatus.Review, reviewer, WorkItemStatus.Ready,
            new(RequireApproval: approval, RequireCommit: evidence, RequirePassingTests: evidence));
        flow.ConfigureTransition(f.Scope, "done", reviewer, WorkItemStatus.Review, null, WorkItemStatus.Done, new());
        db.AddRange(reviewer, agent, review, flow); await db.SaveChangesAsync(Ct); return (flow, agent);
    }
    internal static async Task<WorkItemView> Ready(Fixture f, string key = "run")
    { var item = await f.CreateItem(key); return (await f.Change(item.Id, new StatusChange(WorkItemStatus.Ready))).Item; }
    internal static Task<AgentRunDetails> Start(Fixture f, (WorkflowDefinition Flow, AgentProfile Agent) setup, WorkItemView item, string outcome = "Succeeded")
        => Runs(f).Handle(new StartAgentRun(f.Scope, item.Id, f.Actor,
            new(Guid.NewGuid(), item.Version, setup.Flow.Id, setup.Flow.Version, f.Role.Id, SimulationOutcome: outcome)), Ct);

    // Runs the actual product job/SQL engine, but bypasses Hangfire transport.
    // Each message uses a fresh context; live queue coverage is separate.
    internal static async Task<int> Drain(Fixture f, Guid item, int limit = 100)
    {
        var processed = 0;
        for (; processed < limit; processed++)
        {
            await using var db = f.Factory.CreateDbContext();
            var pending = await db.Deliveries.Include(x => x.Outbox).Where(x => x.WorkItemId == item && x.Outbox.OutboxState == OutboxState.Requested)
                .OrderBy(x => x.OutboxId).FirstOrDefaultAsync(Ct);
            if (pending is null) break;
            var engine = new EfOutboxStore<OrchestrationDbContext>(db); var lease = Guid.NewGuid();
            Assert.NotNull(await engine.ClaimDispatchAsync(pending.OutboxId, lease, TimeSpan.FromMinutes(5), Ct));
            await engine.CompleteDispatchAsync(pending.OutboxId, lease, "simulation-test", null, Ct);
            f.Clock.Advance(1);
            var job = new RunOutboxJob(new(f.Factory), new(new FakeHarness(), f.Clock));
            await job.Execute(pending.OutboxId, Ct); await job.Execute(pending.OutboxId, Ct);
            var state = await engine.GetAsync(pending.OutboxId, Ct);
            Assert.Equal(OutboxState.Processed, state!.OutboxState);
        }
        return processed;
    }
}
