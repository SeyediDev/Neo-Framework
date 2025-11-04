namespace Neo.Application.Features.Outbox;

public record IdempotencyRecord
{
    public string IdempotencyKey { get; set; } = null!;
    public string TenantId { get; set; } = null!;
    public long OutboxId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IIdempotencyStore<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    Task<bool> AddAsync(string idempotencyKey, string tenantId, long outboxId, CancellationToken ct);
    Task<IdempotencyRecord?> GetAsync(string idempotencyKey, string tenantId, CancellationToken ct);
    Task RemoveAsync(string idempotencyKey, string tenantId, CancellationToken ct);
}