using System.Text.Json;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using StackExchange.Redis;

namespace Neo.Infrastructure.Features.Outbox;

public sealed class IdempotencyStoreRedis<TMessage>(IConnectionMultiplexer connection) : IIdempotencyStore<TMessage>
    where TMessage : IOutboxMessage
{
    private static RedisKey Key(string key, string tenant) => $"neo:idempotency:{typeof(TMessage).FullName}:{IdempotencyKeyHasher.CreateStorageKey(tenant, key)}";
    public async Task<bool> AddAsync(string idempotencyKey, string tenantKey, long outboxId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var record = new IdempotencyRecord { IdempotencyKey = IdempotencyKeyHasher.CreateStorageKey(tenantKey, idempotencyKey), TenantKey = tenantKey, OutboxId = outboxId };
        return await connection.GetDatabase().StringSetAsync(Key(idempotencyKey, tenantKey), JsonSerializer.Serialize(record),
            IdempotencyConfigurationHelper.GetTtlOrDefault<TMessage>(30), When.NotExists);
    }
    public async Task<IdempotencyRecord?> GetAsync(string idempotencyKey, string tenantKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = await connection.GetDatabase().StringGetAsync(Key(idempotencyKey, tenantKey));
        return value.IsNull ? null : JsonSerializer.Deserialize<IdempotencyRecord>((string)value!);
    }
    public async Task RemoveAsync(string idempotencyKey, string tenantKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await connection.GetDatabase().KeyDeleteAsync(Key(idempotencyKey, tenantKey));
    }
}
