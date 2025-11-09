using Microsoft.EntityFrameworkCore;
using Neo.Domain.Features.DatabaseCache;

namespace Neo.Infrastructure.Features.DatabaseCache;

public static class DatabaseCacheExtensions
{
    /// <summary>
    /// Cache کردن نتیجه query
    /// </summary>
    public static async Task<T?> CachedAsync<T>(
        this Task<T> query,
        IDatabaseCache cache,
        string key,
        TimeSpan? expiration = null) where T : class
    {
        return await cache.GetOrSetAsync(
            key,
            () => query,
            absoluteExpiration: expiration
        );
    }

    /// <summary>
    /// Cache کردن لیست
    /// </summary>
    public static async Task<List<T>> CachedListAsync<T>(
        this IQueryable<T> query,
        IDatabaseCache cache,
        string key,
        TimeSpan? expiration = null) where T : class
    {
        var result = await cache.GetOrSetAsync(
            key,
            async () => await query.ToListAsync(),
            absoluteExpiration: expiration
        );
        
        return result ?? [];
    }
}