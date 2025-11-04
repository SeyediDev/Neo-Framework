using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using MongoDB.Driver;

namespace Neo.Infrastructure.Features.Outbox;

public class IdempotencyStoreMongoDb<TOutboxMessage> : IIdempotencyStore<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    private readonly IMongoCollection<IdempotencyRecord> _collection;

    public IdempotencyStoreMongoDb(IMongoDatabase database)
    {
        _collection = database.GetCollection<IdempotencyRecord>($"idempotency-{typeof(TOutboxMessage).Name}");

        // ساخت Composite Index روی (TenantId, IdempotencyKey)
        var indexKeys = Builders<IdempotencyRecord>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.IdempotencyKey);
        
        var indexOptions = new CreateIndexOptions { Unique = true };
        var indexModel = new CreateIndexModel<IdempotencyRecord>(indexKeys, indexOptions);

        _collection.Indexes.CreateOne(indexModel);
        
        // اضافه کردن TTL Index برای پاکسازی خودکار رکوردهای قدیمی
        var ttlIndexKeys = Builders<IdempotencyRecord>.IndexKeys.Ascending(x => x.CreatedAt);
        
        // Get TTL from IdempotencyAttribute
        var ttl = IdempotencyConfigurationHelper.GetTtlOrDefault<TOutboxMessage>(defaultTtlDays: 30);
        var ttlIndexOptions = new CreateIndexOptions { ExpireAfter = ttl };
        var ttlIndexModel = new CreateIndexModel<IdempotencyRecord>(ttlIndexKeys, ttlIndexOptions);
        
        _collection.Indexes.CreateOne(ttlIndexModel);
    }

    public async Task<bool> AddAsync(string idempotencyKey, string tenantId, long outboxId, CancellationToken ct)
    {
        try
        {
            // هش کردن کلید برای امنیت و کارایی بهتر
            var hashedKey = IdempotencyKeyHasher.CreateShortHashedKey(tenantId, idempotencyKey);
            
            IdempotencyRecord record = new()
            {
                CreatedAt = DateTime.UtcNow,
                IdempotencyKey = hashedKey,
                TenantId = tenantId,
                OutboxId = outboxId,
            };

            await _collection.InsertOneAsync(record, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // اگر کلید تکراری بود → false
            return false;
        }
    }

    public async Task<IdempotencyRecord?> GetAsync(string idempotencyKey, string tenantId, CancellationToken ct)
    {
        // هش کردن کلید برای جستجو
        var hashedKey = IdempotencyKeyHasher.CreateShortHashedKey(tenantId, idempotencyKey);
        
        return await _collection
            .Find(x => x.IdempotencyKey == hashedKey && x.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RemoveAsync(string idempotencyKey, string tenantId, CancellationToken ct)
    {
        // هش کردن کلید برای حذف
        var hashedKey = IdempotencyKeyHasher.CreateShortHashedKey(tenantId, idempotencyKey);
        
        await _collection.DeleteOneAsync(
            x => x.IdempotencyKey == hashedKey && x.TenantId == tenantId, 
            ct);
    }
}
