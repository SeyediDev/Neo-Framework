using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Application.Advisory;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Advisory;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;

namespace Neo.AgentOrchestration.Infrastructure.Advisory;

public sealed class SqlAdvisoryRequests(IDbContextFactory<OrchestrationDbContext> factory,
    TimeProvider clock, IConfiguration config) : IAdvisoryRequests
{
    public Task<AdvisoryRequestView> CreateAsync(WorkspaceScope scope, Guid itemId, WorkActor actor,
        CreateAdvisoryRequest body, CancellationToken ct) => Transaction(scope, async (db, token) =>
    {
        // Fingerprint original input and authenticated source, not just normalized text.
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { itemId, body, actor.ChatId })));
        var previous = await Query(db, scope).SingleOrDefaultAsync(x => x.RequesterId == actor.AgentId && x.RequestId == body.RequestId, token);
        if (previous is not null)
        {
            if (previous.Fingerprint != hash) throw new DeliveryConflictException("Advisory request key changed.");
            return View(previous); // original stale-version retry is harmless, no new deadline/dispatch
        }
        var item = await db.WorkItems.SingleOrDefaultAsync(x => x.Id == itemId &&
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, token)
            ?? throw new KeyNotFoundException();
        AdvisoryBindings.RequireProject(config, scope, item.ProjectId);
        if (!await db.Projects.AnyAsync(x => x.Id == item.ProjectId && x.IsEnabled, token)) throw new InvalidOperationException();
        // At most one outstanding advisory per user/task. No new provider quota ledger.
        if (await Query(db, scope).AnyAsync(x => x.WorkItemId == itemId && x.RequesterId == actor.AgentId &&
            (x.State == AdvisoryState.Queued || x.State == AdvisoryState.Submitting || x.State == AdvisoryState.Held), token))
            throw new InvalidOperationException("Resolve the outstanding request first.");
        var messageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.AgentId + "\n" + body.RequestId)));
        var staged = await SqlDurableWorkStore.StageAsync(db, scope,
            new(itemId, "agno-advisory-v1", messageId, hash), (_, _) => Task.CompletedTask,
            false, WorkDeliveryKind.SendAdvisoryRequest, clock, token);
        var advisory = AdvisoryRequest.Create(scope, item, actor, body.RequestId, body.ExpectedWorkItemVersion,
            body.Brief, body.Consent, body.BudgetSeconds, hash, staged.Delivery!.Id, clock.GetUtcNow());
        db.Add(advisory); return View(advisory);
    }, ct);

    public async Task<AdvisoryRequestCatalog> ListAsync(WorkspaceScope scope, Guid itemId, WorkActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var projectId = await db.WorkItems.Where(x => x.Id == itemId && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
            .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException();
        var rows = await Query(db, scope).AsNoTracking().Where(x => x.WorkItemId == itemId && x.RequesterId == actor.AgentId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(25).ToArrayAsync(ct);
        var enabled = AdvisoryBindings.SubmissionEnabled(config, scope, projectId) && await db.Projects.AnyAsync(x => x.Id == projectId && x.IsEnabled, ct);
        return new(enabled, rows.Select(View).ToArray());
    }
    public async Task<AdvisoryRequestView> GetAsync(WorkspaceScope scope, Guid id, WorkActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await Query(db, scope).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.RequesterId == actor.AgentId, ct)
            ?? throw new KeyNotFoundException();
        return View(row);
    }
    public Task<AdvisoryRequestView> CancelAsync(WorkspaceScope scope, Guid id, WorkActor actor, Guid version, CancellationToken ct)
        => Transaction(scope, async (db, token) =>
        {
            var row = await Query(db, scope).SingleOrDefaultAsync(x => x.Id == id && x.RequesterId == actor.AgentId, token)
                ?? throw new KeyNotFoundException();
            row.RequireRequester(scope, actor); row.Cancel(version, clock.GetUtcNow()); return View(row);
        }, ct);

    // Trusted Worker only. Persist intent in its own transaction BEFORE HTTP.
    public Task<AdvisoryRequest?> BeginAsync(WorkDeliveryExecution operation, CancellationToken ct)
        => Transaction(operation.Scope, async (db, token) =>
        {
            if (operation.Kind != WorkDeliveryKind.SendAdvisoryRequest || operation.AgentRunId is not null) throw new InvalidOperationException();
            var row = await Query(db, operation.Scope).SingleAsync(x => x.DeliveryOperationId == operation.OperationId &&
                x.ProjectId == operation.ProjectId && x.WorkItemId == operation.WorkItemId, token);
            AdvisoryBindings.RequireProject(config, operation.Scope, row.ProjectId);
            var eligible = await db.WorkItems.AnyAsync(x => x.Id == row.WorkItemId && x.Version == row.WorkItemVersion && !x.IsArchived &&
                x.Status != WorkItemStatus.Cancelled && db.Projects.Any(p => p.Id == x.ProjectId && p.IsEnabled), token);
            return row.Begin(clock.GetUtcNow(), eligible) ? row : null;
        }, ct);

    public Task FinishAsync(WorkspaceScope scope, Guid id, AdvisoryDraft? draft, string reason, bool knownRejection, CancellationToken ct)
        => Transaction(scope, async (db, token) =>
        {
            var row = await Query(db, scope).SingleAsync(x => x.Id == id, token);
            if (draft is not null) row.Complete(draft, clock.GetUtcNow());
            else if (knownRejection) row.Reject(reason, clock.GetUtcNow());
            else row.Hold(reason, clock.GetUtcNow());
            return true;
        }, ct);

    public async Task ReconcileExhaustedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = Delivery.EnabledLaneDispatcher.Enabled(db.Deliveries, config)
            .Where(x => x.Kind == WorkDeliveryKind.SendAdvisoryRequest &&
                x.Outbox.OutboxState == Neo.Domain.Entities.Common.OutboxState.Failed && x.Outbox.DeliveryLeaseId == null).Select(x => x.Id);
        var rows = await db.Set<AdvisoryRequest>().AsNoTracking().Where(x => ids.Contains(x.DeliveryOperationId) &&
            (x.State == AdvisoryState.Queued || x.State == AdvisoryState.Submitting))
            .OrderBy(x => x.CreatedAtUtc).Take(25).Select(x => new { x.Id, x.OrganizationId, x.WorkspaceId }).ToArrayAsync(ct);
        foreach (var row in rows)
            await Transaction(new(row.OrganizationId, row.WorkspaceId), async (session, token) =>
            {
                var request = await session.Set<AdvisoryRequest>().SingleAsync(x => x.Id == row.Id, token);
                request.DeliveryExhausted(clock.GetUtcNow()); return true;
            }, ct);
    }

    private async Task<T> Transaction<T>(WorkspaceScope scope, Func<OrchestrationDbContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await WorkspaceTransaction.LockAsync(db, scope, ct);
        try { var value = await action(db, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return value; }
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException("Advisory changed."); }
    }
    internal static IQueryable<AdvisoryRequest> Query(OrchestrationDbContext db, WorkspaceScope scope) => db.Set<AdvisoryRequest>()
        .Where(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId);
    internal static AdvisoryRequestView View(AdvisoryRequest x) => new(x.Id, x.WorkItemId, x.WorkItemVersion, x.Version,
        x.Brief, x.State.ToString(), x.CancelRequested, x.Reason, x.Draft, x.Model, x.InputTokens, x.OutputTokens,
        x.CreatedAtUtc, x.DeadlineUtc, x.StartedAtUtc, x.FinishedAtUtc);
}
