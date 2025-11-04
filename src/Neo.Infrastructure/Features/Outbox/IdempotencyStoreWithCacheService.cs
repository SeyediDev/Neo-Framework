using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using Neo.Domain.Features.Cache;

namespace Neo.Infrastructure.Features.Outbox;

public class IdempotencyStoreWithCacheService<TOutboxMessage>(
    ICacheService cacheService) : 
    IIdempotencyStore<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    private static string Key(string idempotencyKey, string tenantId) => $"idempotency-{typeof(TOutboxMessage).Name}-{tenantId}-{idempotencyKey}";
    
    public async Task<bool> AddAsync(string idempotencyKey, string tenantId, long outboxId, CancellationToken ct)
    {
        var key = Key(idempotencyKey, tenantId);
        if ((await GetAsync(idempotencyKey, tenantId, ct)) != null)
            return false;
            
        IdempotencyRecord record = new()
        {
            CreatedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey,
            TenantId = tenantId,
            OutboxId = outboxId,
        };
        
        // Get TTL from IdempotencyAttribute
        var ttl = IdempotencyConfigurationHelper.GetTtlOrDefault<TOutboxMessage>(defaultTtlDays: 30);
        
        await cacheService.SetAsync(key, record, new()
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
        return true;
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
