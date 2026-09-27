using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class SqlDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static SqlDurableWorkStore Store(Fixture f) => new(f.Factory, f.Clock);
    private static WorkDeliveryRequest Request(Guid id, string message = "event-1", string payload = "{\"status\":\"completed\"}")
        => new(id, "harness-callback-v1", message, payload);
    private static async Task Note(Fixture f, IWorkItemSession session, Guid id, string text, CancellationToken ct)
        => (await session.GetItemAsync(id, ct))!.AddLog(f.Scope, f.Actor, text, f.Clock.GetUtcNow());

    [Fact]
    public async Task Independent_workspaces_create_tasks_and_receive_callbacks_concurrently()
    {
        var fixtures = new List<Fixture>();
        for (var i = 0; i < 4; i++) fixtures.Add(await Fixture.Create());
        await Task.WhenAll(fixtures.Select(async f =>
        {
            for (var index = 0; index < 5; index++)
            {
                var item = await f.CreateItem($"parallel-{index}");
                var result = await Store(f).ApplyInboxAsync(f.Scope, Request(item.Id, $"event-{index}"),
                    (session, token) => Note(f, session, item.Id, "Parallel callback", token), Ct);
                Assert.False(result.Duplicate);
            }
        }));
        foreach (var f in fixtures)
        {
            await using var db = f.Factory.CreateDbContext();
            Assert.Equal(5, await db.InboxReceipts.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_inbox_applies_once_and_restart_returns_original_receipt()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("inbox"); var calls = 0;
        async Task Apply(IWorkItemSession session, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Note(f, session, item.Id, "Callback applied once", token);
        }
        var results = await Task.WhenAll(
            Store(f).ApplyInboxAsync(f.Scope, Request(item.Id), Apply, Ct, WorkDeliveryKind.EvaluateWorkflow),
            Store(f).ApplyInboxAsync(f.Scope, Request(item.Id), Apply, Ct, WorkDeliveryKind.EvaluateWorkflow));
        Assert.Equal(1, calls); Assert.Single(results, x => !x.Duplicate);
        Assert.Equal(results[0].ReceiptId, results[1].ReceiptId);
        Assert.Equal(results[0].OutboxId, results[1].OutboxId);
        var replay = await Store(f).ApplyInboxAsync(f.Scope, Request(item.Id), Apply, Ct, WorkDeliveryKind.EvaluateWorkflow);
        Assert.True(replay.Duplicate); Assert.Equal(1, calls);
        Assert.Equal(results[0].WorkItemVersion, replay.WorkItemVersion);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(1, await db.InboxReceipts.CountAsync(x => x.WorkItemId == item.Id, Ct));
        Assert.Equal(1, await db.Deliveries.CountAsync(x => x.WorkItemId == item.Id, Ct));
        var persisted = await db.WorkItems.Include(x => x.Logs).SingleAsync(x => x.Id == item.Id, Ct);
        Assert.Single(persisted.Logs, x => x.Message == "Callback applied once");
    }

    [Fact]
    public async Task Reused_key_rejects_changed_payload_target_or_followup_without_an_effect()
    {
        var f = await Fixture.Create(); var a = await f.CreateItem("a"); var b = await f.CreateItem("b"); var calls = 0;
        Task Apply(IWorkItemSession _, CancellationToken token) { token.ThrowIfCancellationRequested(); calls++; return Task.CompletedTask; }
        await Store(f).ApplyInboxAsync(f.Scope, Request(a.Id), Apply, Ct);
        await Assert.ThrowsAsync<DeliveryConflictException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(a.Id, payload: "different"), Apply, Ct));
        await Assert.ThrowsAsync<DeliveryConflictException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(b.Id), Apply, Ct));
        await Assert.ThrowsAsync<DeliveryConflictException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(a.Id), Apply, Ct, WorkDeliveryKind.DispatchAgent));
        Assert.Equal(1, calls);
        // Producer names normalize, opaque message IDs remain case-sensitive in SQL.
        var repeated = await Store(f).ApplyInboxAsync(f.Scope, Request(a.Id) with { Source = " HARNESS-CALLBACK-V1 " }, Apply, Ct);
        Assert.True(repeated.Duplicate);
        await Store(f).ApplyInboxAsync(f.Scope, Request(a.Id, "Event-1"), Apply, Ct);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Rollback_and_cancellation_leave_no_inbox_outbox_or_business_mutation()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("rollback");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(item.Id), async (session, token) =>
        {
            await Note(f, session, item.Id, "Must roll back", token);
            throw new InvalidOperationException("Deliberate failure");
        }, Ct, WorkDeliveryKind.EvaluateWorkflow));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(f).EnqueueAsync(f.Scope, Request(item.Id),
            WorkDeliveryKind.DispatchAgent, async (session, token) =>
            {
                await Note(f, session, item.Id, "Must cancel", token); cancel.Cancel();
            }, cancel.Token));
        await using var db = f.Factory.CreateDbContext();
        Assert.False(await db.InboxReceipts.AnyAsync(x => x.WorkItemId == item.Id, Ct));
        Assert.False(await db.Deliveries.AnyAsync(x => x.WorkItemId == item.Id, Ct));
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.TenantKey == f.Scope.WorkspaceId.ToString("N"), Ct));
        Assert.Equal(item.Version, (await db.WorkItems.SingleAsync(x => x.Id == item.Id, Ct)).Version);
    }

    [Fact]
    public async Task Outgoing_change_and_neo_message_commit_together_with_no_raw_callback_payload()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("outgoing"); var calls = 0;
        var request = Request(item.Id, payload: "private-callback-body");
        async Task Change(IWorkItemSession session, CancellationToken token) { calls++; await Note(f, session, item.Id, "Dispatch requested", token); }
        var first = await Store(f).EnqueueAsync(f.Scope, request, WorkDeliveryKind.DispatchAgent, Change, Ct);
        var second = await Store(f).EnqueueAsync(f.Scope, request, WorkDeliveryKind.DispatchAgent, Change, Ct);
        Assert.Equal(first.OutboxId, second.OutboxId); Assert.Equal(1, calls); Assert.True(second.Duplicate);
        await using var db = f.Factory.CreateDbContext();
        var row = await db.Deliveries.Include(x => x.Outbox).SingleAsync(x => x.Id == first.ReceiptId, Ct);
        Assert.Equal(OutboxState.Requested, row.Outbox.OutboxState);
        Assert.Equal(64, row.PayloadHash.Length);
        Assert.DoesNotContain("private-callback-body", row.Outbox.MessageContent);
        Assert.Contains(row.Id.ToString(), row.Outbox.MessageContent);
        Assert.Equal(first.WorkItemVersion, (await db.WorkItems.SingleAsync(x => x.Id == item.Id, Ct)).Version);
    }

    [Fact]
    public async Task Competing_dispatchers_and_fast_worker_cannot_overwrite_processed_state()
    {
        var f = await Fixture.Create(); var acceptance = await Enqueue(f); var id = acceptance.OutboxId!.Value;
        await using var a = f.Factory.CreateDbContext(); await using var b = f.Factory.CreateDbContext();
        var tokenA = Guid.NewGuid(); var tokenB = Guid.NewGuid();
        var storeA = new EfOutboxStore<OrchestrationDbContext>(a); var storeB = new EfOutboxStore<OrchestrationDbContext>(b);
        var claims = await Task.WhenAll(storeA.ClaimDispatchAsync(id, tokenA, TimeSpan.FromMinutes(5), Ct),
            storeB.ClaimDispatchAsync(id, tokenB, TimeSpan.FromMinutes(5), Ct));
        Assert.Single(claims, x => x is not null);
        var executor = new SqlWorkDeliveryExecutor(f.Factory); var executions = 0;
        var result = await executor.ExecuteAsync(id, async (execution, session, token) =>
        {
            executions++; await Note(f, session, execution.WorkItemId, "Worker effect", token);
        }, Ct);
        Assert.Equal(DeliveryExecutionOutcome.Processed, result);
        await storeA.CompleteDispatchAsync(id, claims[0] is not null ? tokenA : tokenB, "late-job", null, Ct);
        Assert.Equal(OutboxState.Processed, (await storeA.GetAsync(id, Ct))!.OutboxState);
        Assert.Equal(DeliveryExecutionOutcome.AlreadyProcessed, await executor.ExecuteAsync(id, (_, _, _) =>
        { executions++; return Task.CompletedTask; }, Ct));
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Handler_failures_roll_back_then_retry_and_retain_terminal_failure_without_secrets()
    {
        var f = await Fixture.Create(); var acceptance = await Enqueue(f); var id = acceptance.OutboxId!.Value;
        await Queue(f, id);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Assert.ThrowsAsync<DeliveryProcessingException>(() => new SqlWorkDeliveryExecutor(f.Factory).ExecuteAsync(id,
                async (execution, session, token) =>
                {
                    await Note(f, session, execution.WorkItemId, "Should never commit", token);
                    throw new InvalidOperationException("provider-secret-value");
                }, Ct));
            await using var db = f.Factory.CreateDbContext();
            var row = await db.OutboxMessages.SingleAsync(x => x.Id == id, Ct);
            Assert.Equal(attempt, row.ProcessTryCount);
            Assert.Equal(attempt == 3 ? OutboxState.Failed : OutboxState.ExecutionRetrying, row.OutboxState);
            Assert.DoesNotContain("provider-secret-value", row.ProcessError!);
            Assert.Null(row.DeliveryLeaseId); Assert.Null(row.DeliveryLeaseUntilUtc);
            Assert.Equal(0, await db.Set<Neo.AgentOrchestration.Domain.Work.WorkItemLog>().CountAsync(x =>
                x.WorkItemId == acceptance.WorkItemId && x.Message == "Should never commit", Ct));
            if (attempt < 3) await db.OutboxMessages.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, x => DateTime.UtcNow.AddMinutes(-1)), Ct);
        }
        Assert.Equal(DeliveryExecutionOutcome.NotReady, await new SqlWorkDeliveryExecutor(f.Factory).ExecuteAsync(id,
            (_, _, _) => throw new InvalidOperationException("Exhausted message must not execute"), Ct));
    }

    [Fact]
    public async Task Expired_leases_are_recovered_and_old_owners_cannot_complete_or_execute()
    {
        var f = await Fixture.Create(); var acceptance = await Enqueue(f); var id = acceptance.OutboxId!.Value;
        await using var db = f.Factory.CreateDbContext(); var engine = new EfOutboxStore<OrchestrationDbContext>(db);
        var old = Guid.NewGuid(); var replacement = Guid.NewGuid();
        Assert.NotNull(await engine.ClaimDispatchAsync(id, old, TimeSpan.FromMinutes(5), Ct));
        await Expire(db, id);
        Assert.NotNull(await engine.ClaimDispatchAsync(id, replacement, TimeSpan.FromMinutes(5), Ct));
        await engine.CompleteDispatchAsync(id, old, "stale-job", null, Ct);
        Assert.Equal(replacement, (await engine.GetAsync(id, Ct))!.DeliveryLeaseId);
        var executionLease = Guid.NewGuid();
        Assert.NotNull(await engine.ClaimExecutionAsync(id, executionLease, TimeSpan.FromMinutes(5), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteClaimedAsync(id, old,
            (_, _) => throw new Exception("Old owner must not execute"), Ct));
        Assert.Equal(executionLease, (await engine.GetAsync(id, Ct))!.DeliveryLeaseId);
        await Expire(db, id);
        var count = 0;
        Assert.Equal(DeliveryExecutionOutcome.Processed, await new SqlWorkDeliveryExecutor(f.Factory).ExecuteAsync(id,
            (_, _, _) => { count++; return Task.CompletedTask; }, Ct));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Final_crashed_dispatch_becomes_failed_instead_of_an_unbounded_retry()
    {
        var f = await Fixture.Create(); var acceptance = await Enqueue(f); var id = acceptance.OutboxId!.Value;
        await using var db = f.Factory.CreateDbContext(); var engine = new EfOutboxStore<OrchestrationDbContext>(db);
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(await engine.ClaimDispatchAsync(id, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
            await Expire(db, id);
        }
        await engine.GetRequested(1, Ct);
        Assert.Equal(OutboxState.Failed, (await engine.GetAsync(id, Ct))!.OutboxState);
        Assert.Null(await engine.ClaimDispatchAsync(id, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task Workspace_isolation_and_invalid_delivery_inputs_fail_before_mutating()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("scope"); var other = await Fixture.Create(); var count = 0;
        Task Apply(IWorkItemSession _, CancellationToken token) { token.ThrowIfCancellationRequested(); count++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Store(f).ApplyInboxAsync(other.Scope, Request(item.Id), Apply, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(Guid.Empty), Apply, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store(f).ApplyInboxAsync(f.Scope, Request(item.Id, payload: new string('x', 262145)), Apply, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store(f).EnqueueAsync(f.Scope, Request(item.Id), (WorkDeliveryKind)255, Apply, Ct));
        Assert.Equal(0, count);
        var a = await Store(f).ApplyInboxAsync(f.Scope, Request(item.Id), Apply, Ct);
        var otherItem = await other.CreateItem("scope");
        var b = await Store(other).ApplyInboxAsync(other.Scope, Request(otherItem.Id), Apply, Ct);
        Assert.NotEqual(a.ReceiptId, b.ReceiptId); Assert.False(b.Duplicate);
    }

    private static async Task<DeliveryAcceptance> Enqueue(Fixture f)
    {
        var item = await f.CreateItem("queued");
        return await Store(f).EnqueueAsync(f.Scope, Request(item.Id), WorkDeliveryKind.EvaluateWorkflow,
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, Ct);
    }
    private static async Task Queue(Fixture f, long id)
    {
        await using var db = f.Factory.CreateDbContext(); var engine = new EfOutboxStore<OrchestrationDbContext>(db); var lease = Guid.NewGuid();
        Assert.NotNull(await engine.ClaimDispatchAsync(id, lease, TimeSpan.FromMinutes(5), Ct));
        await engine.CompleteDispatchAsync(id, lease, "verified-job", null, Ct);
    }
    private static Task<int> Expire(OrchestrationDbContext db, long id) => db.OutboxMessages.Where(x => x.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryLeaseUntilUtc, x => DateTime.UtcNow.AddMinutes(-1)), Ct);
}
