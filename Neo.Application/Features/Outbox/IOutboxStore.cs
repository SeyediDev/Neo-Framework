namespace Neo.Application.Features.Outbox;

public interface IOutboxStore
{
    Task AddAsync(OutboxMessage outboxMessage, CancellationToken ct);
    Task UpdateAsync(OutboxMessage outboxMessage, CancellationToken ct);
    void UpdateOnlyAsync(OutboxMessage outboxMessage);
    Task SaveChangesAsync(CancellationToken ct);
    Task FinishAsync(OutboxMessage outboxMessage, CancellationToken ct);
    Task<OutboxMessage?> GetAsync(long outboxId, CancellationToken ct);
    Task<OutboxResponse?> GetOutboxResponseAsync(long outboxId, CancellationToken ct);
    Task<MessageState?> GetStatusAsync(long outboxId, CancellationToken ct);
    Task<IEnumerable<OutboxMessage>> GetRequested(int batchSize, CancellationToken cancellationToken);
}