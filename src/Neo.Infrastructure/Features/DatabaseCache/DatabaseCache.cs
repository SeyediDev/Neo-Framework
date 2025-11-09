using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Neo.Domain.Features.DatabaseCache;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neo.Infrastructure.Features.DatabaseCache;

public class DatabaseCache(IMemoryCache cache, ILogger<DatabaseCache> logger) : IDatabaseCache
{
    private static readonly Dictionary<string, string> _versionCache = [];
    private static readonly object _lock = new();

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? absoluteExpiration = null,
        TimeSpan? slidingExpiration = null) where T : class
    {
        // 1. بررسی Cache
        var cacheKey = GenerateCacheKey<T>(key);
        
        if (cache.TryGetValue(cacheKey, out CachedItem<T>? cachedItem))
        {
            logger.LogDebug("Cache HIT for key: {Key}", key);
            return cachedItem!.Data;
        }

        logger.LogDebug("Cache MISS for key: {Key}", key);

        // 2. خواندن از دیتابیس
        var data = await factory();
        if (data == null) return null;

        // 3. محاسبه Version (مثل ETag)
        var version = ComputeVersion(data);

        // 4. ذخیره در Cache
        var cacheOptions = new MemoryCacheEntryOptions();
        
        if (absoluteExpiration.HasValue)
            cacheOptions.SetAbsoluteExpiration(absoluteExpiration.Value);
        
        if (slidingExpiration.HasValue)
            cacheOptions.SetSlidingExpiration(slidingExpiration.Value);
        else
            cacheOptions.SetSlidingExpiration(TimeSpan.FromMinutes(30)); // Default

        // ثبت callback برای وقتی که cache منقضی می‌شود
        cacheOptions.RegisterPostEvictionCallback((key, value, reason, state) =>
        {
            logger.LogDebug("Cache evicted: {Key}, Reason: {Reason}", key, reason);
        });

        var item = new CachedItem<T>
        {
            Data = data,
            Version = version,
            CachedAt = DateTime.UtcNow
        };

        cache.Set(cacheKey, item, cacheOptions);

        // ذخیره version برای invalidation
        lock (_lock)
        {
            _versionCache[key] = version;
        }

        return data;
    }

    public void Invalidate(string key)
    {
        var cacheKey = $"DbCache_{key}";
        cache.Remove(cacheKey);
        
        lock (_lock)
        {
            _versionCache.Remove(key);
        }
        
        logger.LogInformation("Cache invalidated for key: {Key}", key);
    }

    public void InvalidateByPattern(string pattern)
    {
        // برای invalidate کردن چند cache به صورت یکجا
        // مثلاً: "Product_*" → تمام محصولات
        lock (_lock)
        {
            var keysToRemove = _versionCache.Keys
                .Where(k => k.StartsWith(pattern.Replace("*", "")))
                .ToList();

            foreach (var key in keysToRemove)
            {
                Invalidate(key);
            }
            logger.LogInformation("Cache invalidated by pattern: {Pattern}, Count: {Count}",
                pattern, keysToRemove.Count);
        }
    }

    private static string ComputeVersion<T>(T data)
    {
        // محاسبه "اثر انگشت" داده (مثل ETag)
        var json = JsonSerializer.Serialize(data);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }

    private static string GenerateCacheKey<T>(string key)
    {
        return $"DbCache_{typeof(T).Name}_{key}";
    }
}