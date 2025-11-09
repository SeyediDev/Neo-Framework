using Microsoft.Extensions.Logging;
using Neo.Domain.Features.DatabaseCache;

namespace Neo.Infrastructure.Features.DatabaseCache;

/// <summary>
/// Extension Methods برای استفاده آسان
/// </summary>
/// <summary>
/// No-Op implementation برای غیرفعال کردن کش
/// هیچ کاری نمی‌کند، فقط مستقیماً query را اجرا می‌کند
/// </summary>
public class NoOpDatabaseCache(ILogger<NoOpDatabaseCache> logger) : IDatabaseCache
{
    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? absoluteExpiration = null,
        TimeSpan? slidingExpiration = null) where T : class
    {
        logger.LogDebug("Database Cache is DISABLED. Executing query directly: {Key}", key);
        return await factory();
    }

    public void Invalidate(string key)
    {
        // No-op
    }

    public void InvalidateByPattern(string pattern)
    {
        // No-op
    }
}

