using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.DatabaseCache;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neo.Infrastructure.Features.DatabaseCache;

/// <summary>
/// Hybrid Cache: MemoryCache + DistributedCache (بهترین از هر دو دنیا!)
/// </summary>
public class HybridDatabaseCache(
    IMemoryCache memoryCache,
    IDistributedCache distributedCache,
    ILogger<HybridDatabaseCache> logger) : IDatabaseCache
{
    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? absoluteExpiration = null,
        TimeSpan? slidingExpiration = null) where T : class
    {
        var cacheKey = $"DbCache_{typeof(T).Name}_{key}";

        // L1: بررسی Memory Cache (خیلی سریع - 0.01ms)
        if (memoryCache.TryGetValue(cacheKey, out T? cachedData) && cachedData != null)
        {
            logger.LogDebug("L1 Cache (Memory) HIT for key: {Key}", key);
            return cachedData;
        }

        // L2: بررسی Distributed Cache (سریع - 1-5ms)
        var cachedBytes = await distributedCache.GetAsync(cacheKey);
        if (cachedBytes != null)
        {
            try
            {
                var cachedItem = JsonSerializer.Deserialize<CachedItem<T>>(cachedBytes);
                if (cachedItem?.Data != null)
                {
                    logger.LogDebug("L2 Cache (Redis) HIT for key: {Key}", key);
                    
                    // ذخیره در L1 (Memory) برای دفعات بعد
                    var memOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5)); // کوتاه‌تر از Redis
                    memoryCache.Set(cacheKey, cachedItem.Data, memOptions);
                    
                    return cachedItem.Data;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize from L2 cache: {Key}", key);
            }
        }

        logger.LogDebug("Cache MISS (L1+L2) for key: {Key}", key);

        // L3: خواندن از دیتابیس (کند - 50ms+)
        var data = await factory();
        if (data == null) return null;

        var version = ComputeVersion(data);
        var item = new CachedItem<T>
        {
            Data = data,
            Version = version,
            CachedAt = DateTime.UtcNow
        };

        // ذخیره در L1 (Memory)
        var memCacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(slidingExpiration ?? TimeSpan.FromMinutes(5));
        memoryCache.Set(cacheKey, data, memCacheOptions);

        // ذخیره در L2 (Redis)
        try
        {
            var distCacheOptions = new DistributedCacheEntryOptions();
            if (absoluteExpiration.HasValue)
                distCacheOptions.SetAbsoluteExpiration(absoluteExpiration.Value);
            else if (slidingExpiration.HasValue)
                distCacheOptions.SetSlidingExpiration(slidingExpiration.Value);
            else
                distCacheOptions.SetSlidingExpiration(TimeSpan.FromMinutes(30));

            var serialized = JsonSerializer.SerializeToUtf8Bytes(item);
            await distributedCache.SetAsync(cacheKey, serialized, distCacheOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store in L2 cache: {Key}", key);
        }

        return data;
    }

    public async void Invalidate(string key)
    {
        var cacheKey = $"DbCache_{key}";
        
        // L1: حذف از Memory
        memoryCache.Remove(cacheKey);
        
        // L2: حذف از Redis
        try
        {
            await distributedCache.RemoveAsync(cacheKey);
            logger.LogInformation("Hybrid Cache (L1+L2) invalidated for key: {Key}", key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to invalidate L2 cache for key: {Key}", key);
        }
    }

    public void InvalidateByPattern(string pattern)
    {
        logger.LogWarning("Pattern invalidation not fully implemented in Hybrid cache: {Pattern}", pattern);
    }

    private static string ComputeVersion<T>(T data)
    {
        var json = JsonSerializer.Serialize(data);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }
}