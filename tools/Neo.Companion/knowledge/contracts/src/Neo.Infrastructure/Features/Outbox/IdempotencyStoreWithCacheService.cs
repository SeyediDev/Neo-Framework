using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using Neo.Domain.Features.Cache;

namespace Neo.Infrastructure.Features.Outbox;

public class IdempotencyStoreWithCacheService<TOutboxMessage>(
    ICacheService cacheService) : 
    IIdempotencyStore<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    private static string Key(string idempotencyKey, string tenantId) => $"neo:idempotency:{typeof(TOutboxMessage).FullName}:{IdempotencyKeyHasher.CreateStorageKey(tenantId, idempotencyKey)}";
    
    public async Task<bool> AddAsync(string idempotencyKey, string tenantId, long outboxId, CancellationToken ct)
    {
        var key = Key(idempotencyKey, tenantId);
        if (cacheService is not IAtomicCacheService atomic)
            throw new InvalidOperationException("Idempotency requires atomic add-if-absent. Use MemoryCacheService for local development, or the Redis/Mongo idempotency store for multiple workers.");
            
        IdempotencyRecord record = new()
        {
            CreatedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey,
            TenantKey = tenantId,
            OutboxId = outboxId,
        };
        
        // Get TTL from IdempotencyAttribute
        var ttl = IdempotencyConfigurationHelper.GetTtlOrDefault<TOutboxMessage>(defaultTtlDays: 30);
        
        return await atomic.TryAddAsync(key, record, ttl, ct);
    }

    public async Task<IdempotencyRecord?> GetAsync(string idempotencyKey, string tenantId, CancellationToken ct)
    {
        var key = Key(idempotencyKey, tenantId);
        return await cacheService.GetAsync<IdempotencyRecord>(key, ct);
    }

    public async Task RemoveAsync(string idempotencyKey, string tenantId, CancellationToken ct)
    {
        var key = Key(idempotencyKey, tenantId);
        await cacheService.RemoveAsync(key, ct);
    }
}
