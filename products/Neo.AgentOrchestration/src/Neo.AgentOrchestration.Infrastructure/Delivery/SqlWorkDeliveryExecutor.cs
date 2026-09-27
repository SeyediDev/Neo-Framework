using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;

namespace Neo.AgentOrchestration.Infrastructure.Delivery;

public enum DeliveryExecutionOutcome { Processed, AlreadyProcessed, NotReady }
public sealed class DeliveryProcessingException() : InvalidOperationException("Delivery processing failed; inspect the operation before replay.");

// Trusted installation worker only, not a user-facing unscoped API. Neo owns
// CAS leases, recovery, bounded retries and Failed (dead-letter) state. A fresh
// context and the SAME business transaction are essential to that guarantee.
public sealed class SqlWorkDeliveryExecutor(IDbContextFactory<OrchestrationDbContext> factory)
{
    public async Task<DeliveryExecutionOutcome> ExecuteAsync(long outboxId,
        Func<WorkDeliveryExecution, IWorkItemSession, CancellationToken, Task> handler, CancellationToken ct,
        Func<WorkDeliveryExecution, CancellationToken, Task>? external = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        await using var db = await factory.CreateDbContextAsync(ct);
        var operation = await db.Deliveries.AsNoTracking().Include(x => x.Outbox).SingleOrDefaultAsync(x => x.OutboxId == outboxId, ct)
            ?? throw new KeyNotFoundException("Delivery not found.");
        if (operation.Outbox.OutboxState == OutboxState.Processed) return DeliveryExecutionOutcome.AlreadyProcessed;
        var delivery = new EfOutboxStore<OrchestrationDbContext>(db);
        var leaseId = Guid.NewGuid();
        var claimed = await delivery.ClaimExecutionAsync(outboxId, leaseId, TimeSpan.FromMinutes(5), ct);
        if (claimed is null)
            return DeliveryExecutionOutcome.NotReady;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var execution = new WorkDeliveryExecution(operation.Id, new(operation.OrganizationId, operation.WorkspaceId),
            operation.ProjectId, operation.WorkItemId, operation.WorkItemVersion, operation.Kind, operation.AgentRunId);
        var externalFailed = false;
        if (operation.Kind == WorkDeliveryKind.SendHarnessRequest)
        {
            try
            {
                ValidateEnvelope(claimed, operation);
                if (external is null) throw new DeliveryProcessingException();
                await external(execution, timeout.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { externalFailed = true; }
        }
        // Record a sanitized failure through Neo's normal fenced retry path,
        // even when the network request was cancelled. No alternate retry ledger.
        using var failureRecording = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await delivery.ExecuteClaimedAsync(outboxId, leaseId, async (row, token) =>
        {
            try
            {
                if (externalFailed) throw new DeliveryProcessingException();
                ValidateEnvelope(row, operation);
                var scope = new WorkspaceScope(operation.OrganizationId, operation.WorkspaceId);
                await WorkspaceTransaction.LockAsync(db, scope, token);
                var session = new SqlWorkspaceWorkStore.Session(db, scope);
                var item = await session.GetItemAsync(operation.WorkItemId, token);
                if (item?.ProjectId != operation.ProjectId ||
                    await session.GetProjectAsync(operation.ProjectId, token) is not { IsEnabled: true })
                    throw new DeliveryProcessingException();
                await handler(new(operation.Id, scope, operation.ProjectId, operation.WorkItemId, operation.WorkItemVersion, operation.Kind, operation.AgentRunId), session, token);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw new OperationCanceledException("Delivery execution cancelled.", token); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Neo persists error.Message. Never pass provider responses,
                // callback bodies or credentials through that field.
                throw new DeliveryProcessingException();
            }
        }, externalFailed ? failureRecording.Token : timeout.Token);
        return DeliveryExecutionOutcome.Processed;
    }
    private static void ValidateEnvelope(OutboxMessage row, DeliveryRecord operation)
    {
        if (row.MessageType != typeof(WorkDeliverySignal).FullName ||
            row.TenantKey != operation.WorkspaceId.ToString("N") || row.IdempotencyKey != operation.Id.ToString("N") ||
            JsonSerializer.Deserialize<WorkDeliverySignal>(row.MessageContent)?.OperationId != operation.Id)
            throw new DeliveryProcessingException();
    }
}
