using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Infrastructure.Advisory;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Application.Features.Outbox;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;

namespace Neo.AgentOrchestration.Infrastructure.Delivery;

// Selection precedes Neo CAS dispatch claims. Disabled/imported lanes are never
// leased, retried or terminalized by an advisory-only installation.
public sealed class EnabledLaneDispatcher(OrchestrationDbContext db, IConfiguration config,
    IOutboxJobScheduler scheduler, SqlAdvisoryRequests? advisory = null) : IProcessOutboxRecurringJob
{
    public static IQueryable<DeliveryRecord> Enabled(IQueryable<DeliveryRecord> rows, IConfiguration config)
    {
        var coding = config.GetValue<bool>("Orchestration:SimulationEnabled") || config.GetValue<bool>("Harness:Enabled");
        var projectIds = config.GetValue<bool>("AgnoAdvisory:Enabled")
            ? config.GetSection("AgnoAdvisory:Bindings").GetChildren()
                .Select(x => Guid.TryParse(x["ProjectId"], out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).ToArray()
            : [];
        return rows.Where(x => coding && x.Kind != WorkDeliveryKind.SendAdvisoryRequest ||
            x.Kind == WorkDeliveryKind.SendAdvisoryRequest && projectIds.Contains(x.ProjectId));
    }
    public async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var ids = Enabled(db.Deliveries, config).Select(x => x.OutboxId);
        var rows = db.Set<OutboxMessage>().Where(x => ids.Contains(x.Id) && !x.IsDeleted);
        await rows.Where(x => (x.OutboxState == OutboxState.Processing && x.ProcessTryCount >= 3 ||
            x.OutboxState == OutboxState.Dispatching && x.PublishTryCount >= 3) && x.DeliveryLeaseUntilUtc <= DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, OutboxState.Failed)
                .SetProperty(x => x.ProcessError, "Final delivery lease expired; reconcile before replay.")
                .SetProperty(x => x.DeliveryLeaseId, (Guid?)null).SetProperty(x => x.DeliveryLeaseUntilUtc, (DateTime?)null), ct);
        if (advisory is not null) await advisory.ReconcileExhaustedAsync(ct);
        var candidates = await rows.AsNoTracking().Where(x =>
            (x.OutboxState == OutboxState.Requested || x.OutboxState == OutboxState.Retrying || x.OutboxState == OutboxState.ExecutionRetrying)
                && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= DateTime.UtcNow) ||
            (x.OutboxState == OutboxState.Dispatching || x.OutboxState == OutboxState.Processing) && x.DeliveryLeaseUntilUtc <= DateTime.UtcNow)
            .OrderBy(x => x.Id).Take(15).ToArrayAsync(ct);
        var delivery = new EfOutboxStore<OrchestrationDbContext>(db);
        foreach (var candidate in candidates)
        {
            var lease = Guid.NewGuid();
            var row = await delivery.ClaimDispatchAsync(candidate.Id, lease, TimeSpan.FromMinutes(5), ct);
            if (row is null) continue;
            string? job = null, error = null;
            try { job = await scheduler.ScheduleOutboxMessageAsync(row, ct); if (string.IsNullOrWhiteSpace(job)) error = "Queue unavailable."; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = "Queue submission failed."; }
            await delivery.CompleteDispatchAsync(row.Id, lease, job, error, ct);
        }
    }
}
