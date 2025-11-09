namespace Neo.Domain.Features.DatabaseCache;

/// <summary>
/// Version-Based Database Caching with automatic invalidation
/// مثل ETag برای HTTP، اما برای Database!
/// </summary>
public interface IDatabaseCache
{
    Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? absoluteExpiration = null,
        TimeSpan? slidingExpiration = null) where T : class;

    void Invalidate(string key);
    void InvalidateByPattern(string pattern);
}

