using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Invoked by the product's existing Hangfire/outbox path, never by HTTP ingress.
public sealed class GatewayOutboxJob(IDbContextFactory<GatewayDbContext> factory, GatewayExecution execution, TimeProvider clock)
{
    public const int MaxObservations = 120;
    [AutomaticRetry(Attempts = 0), Queue("outbox")]
    public async Task Execute(long outboxId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var activation = await db.Activations.AsNoTracking().Include(x => x.Outbox)
            .SingleOrDefaultAsync(x => x.OutboxId == outboxId, ct) ?? throw new GatewayJobException();
        Validate(activation.Outbox, activation);
        if (activation.Outbox.OutboxState == OutboxState.Processed) return;
        var outbox = new EfOutboxStore<GatewayDbContext>(db); var lease = Guid.NewGuid();
        if (await outbox.ClaimExecutionAsync(outboxId, lease, TimeSpan.FromMinutes(5), ct) is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Shorter than the journal lease. Native work is asynchronous, not held
        // inside this queue job or a SQL transaction for the model's lifetime.
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var failed = false; var capacityWait = false;
        try
        {
            await execution.AdvanceAsync(activation.Scope, timeout.Token);
        }
        catch (GatewayConflictException ex) when (ex.Code == "gateway-capacity-held") { capacityWait = true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failed = true; }
        using var recording = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await outbox.ExecuteClaimedAsync(outboxId, lease, async (row, token) =>
        {
            Validate(row, activation);
            if (failed) throw new GatewayJobException();
            await SqlGatewayJournal.LockAsync(db, token);
            var run = await db.Runs.SingleAsync(x => x.RunId == activation.RunId &&
                x.OrganizationId == activation.OrganizationId && x.WorkspaceId == activation.WorkspaceId && x.ProjectId == activation.ProjectId, token);
            var continueMonitoring = capacityWait || run.Phase is GatewayPhase.Reserved or GatewayPhase.Prepared or
                GatewayPhase.Running or GatewayPhase.StopRequested or GatewayPhase.AwaitingEvidence or GatewayPhase.CallbackReady;
            if (!continueMonitoring) return;
            // Bounded queue activity. Exhaustion becomes a Neo Failed delivery;
            // it never reports model failure or frees possibly-live capacity.
            if (activation.Sequence + 1 >= MaxObservations) throw new GatewayJobException("gateway-observation-budget-exhausted");
            if (!await db.Activations.AnyAsync(x => x.RunId == run.RunId && x.Sequence == activation.Sequence + 1, token))
                GatewayActivation.Stage(db, run, activation.Sequence + 1, clock.GetUtcNow().AddSeconds(30));
        }, recording.Token);
    }
    private static void Validate(OutboxMessage row, GatewayActivation activation)
    {
        try
        {
            if (activation.Sequence is < 0 or >= MaxObservations || row.Id != activation.OutboxId || row.MessageType != typeof(GatewayAdvanceSignal).FullName ||
                row.TenantKey != activation.WorkspaceId.ToString("N") || row.IdempotencyKey != activation.Id.ToString("N") ||
                JsonSerializer.Deserialize<GatewayAdvanceSignal>(row.MessageContent)?.ActivationId != activation.Id)
                throw new GatewayJobException();
        }
        catch (JsonException) { throw new GatewayJobException(); }
    }
}

public sealed class GatewayJobException(string code = "gateway-delivery-requires-inspection") : InvalidOperationException(code);
