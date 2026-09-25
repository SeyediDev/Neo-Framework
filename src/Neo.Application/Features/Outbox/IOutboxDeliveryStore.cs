namespace Neo.Application.Features.Outbox;

/// <summary>Database compare-and-set leases and a shared business/execution-result transaction.</summary>
public interface IOutboxDeliveryStore : IOutboxStore
{
    Task<OutboxMessage?> ClaimDispatchAsync(long id, Guid leaseId, TimeSpan lease, CancellationToken ct);
    Task CompleteDispatchAsync(long id, Guid leaseId, string? jobId, string? error, CancellationToken ct);
    Task<OutboxMessage?> ClaimExecutionAsync(long id, Guid leaseId, TimeSpan lease, CancellationToken ct);
    Task ExecuteClaimedAsync(long id, Guid leaseId, Func<OutboxMessage, CancellationToken, Task> handler, CancellationToken ct);
}
