using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.DatabaseCache;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neo.Infrastructure.Features.DatabaseCache;

/// <summary>
/// Version-Based Database Caching با Distributed Cache (Redis)
/// برای استفاده در Docker/Kubernetes/Multi-Server
/// </summary>
public class DistributedDatabaseCache(
    IDistributedCache cache,
    ILogger<DistributedDatabaseCache> logger) : IDatabaseCache
{
    private static readonly DistributedCacheEntryOptions DefaultOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(30)
    };

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? absoluteExpiration = null,
        TimeSpan? slidingExpiration = null) where T : class
    {
        var cacheKey = GenerateCacheKey<T>(key);

        // 1. تلاش برای خواندن از Cache
        var cachedBytes = await cache.GetAsync(cacheKey);
        
        if (cachedBytes != null)
        {
            try
            {
                var cachedItem = JsonSerializer.Deserialize<CachedItem<T>>(cachedBytes);
                if (cachedItem?.Data != null)
                {
                    logger.LogDebug("Distributed Cache HIT for key: {Key}", key);
                    return cachedItem.Data;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize cache for key: {Key}", key);
                await cache.RemoveAsync(cacheKey);
            }
        }

        logger.LogDebug("Distributed Cache MISS for key: {Key}", key);

        // 2. خواندن از دیتابیس
        var data = await factory();
        if (data == null) return null;

        // 3. محاسبه Version
        var version = ComputeVersion(data);

        // 4. ذخیره در Distributed Cache
        var cacheOptions = new DistributedCacheEntryOptions();
        
        if (absoluteExpiration.HasValue)
            cacheOptions.SetAbsoluteExpiration(absoluteExpiration.Value);
        
        if (slidingExpiration.HasValue)
            cacheOptions.SetSlidingExpiration(slidingExpiration.Value);
        else
            cacheOptions.SetSlidingExpiration(TimeSpan.FromMinutes(30));

        var item = new CachedItem<T>
        {
            Data = data,
            Version = version,
            CachedAt = DateTime.UtcNow
        };

        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(item);
            await cache.SetAsync(cacheKey, serialized, cacheOptions);
            logger.LogDebug("Stored in Distributed Cache: {Key}, Size: {Size} bytes", 
                key, serialized.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store in Distributed Cache: {Key}", key);
            // اگر Redis down باشد، داده را return می‌کنیم (graceful degradation)
        }

        return data;
    }

    public async void Invalidate(string key)
    {
        try
        {
            var cacheKey = $"DbCache_{key}";
            await cache.RemoveAsync(cacheKey);
            logger.LogInformation("Distributed Cache invalidated for key: {Key}", key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to invalidate Distributed Cache for key: {Key}", key);
        }
    }

    public void InvalidateByPattern(string pattern)
    {
        // Redis pattern matching برای invalidation
        logger.LogWarning(
            "Pattern-based invalidation ({Pattern}) requires Redis SCAN command. " +
            "Consider implementing IConnectionMultiplexer for better performance.",
            pattern);
        
        // برای حالت ساده، فقط لاگ می‌کنیم
        // در production باید از Redis SCAN استفاده کنید
    }

    private static string ComputeVersion<T>(T data)
    {
        var json = JsonSerializer.Serialize(data);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }

    private static string GenerateCacheKey<T>(string key)
    {
        return $"DbCache_{typeof(T).Name}_{key}";
    }
}