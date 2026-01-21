namespace Neo.Application.Features.Outbox;

public record IdempotencyRecord
{
    public string IdempotencyKey { get; set; } = null!;
    public string TenantKey { get; set; } = null!;
    public long OutboxId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IIdempotencyStore<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    Task<bool> AddAsync(string idempotencyKey, string tenantKey, long outboxId, CancellationToken ct);
    Task<IdempotencyRecord?> GetAsync(string idempotencyKey, string tenantKey, CancellationToken ct);
    Task RemoveAsync(string idempotencyKey, string tenantKey, CancellationToken ct);
}