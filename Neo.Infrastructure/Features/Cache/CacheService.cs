using Neo.Common.Extensions;
using Neo.Domain.Features.Cache;
using Microsoft.Extensions.Caching.Distributed;

namespace Neo.Infrastructure.Features.Cache;
public class CacheService(IDistributedCache cache) : ICacheService
{
    public T? Get<T>(string key)
    {
        var value = cache?.GetString(key);
        return value != null ? value.FromJson<T>() : default;
    }
    public void Set<T>(string key, T? value)
    {
        cache.SetString(key, value.ToJson());
    }

    public void Set<T>(string key, T? value, TimeSpan lifeTime)
    {
        cache.SetString(key, value.ToJson(), new DistributedCacheEntryOptions() { AbsoluteExpirationRelativeToNow = lifeTime });
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        string? cashedValue = await cache.GetStringAsync(key, cancellationToken);
        if (cashedValue is null)
        {
            return default;
        }
        T? value = cashedValue.FromJson<T>();
        return value;
    }

    public async Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions option, CancellationToken cancellationToken = default)
    {
        var cacheValue = value.ToJson();
        await cache.SetStringAsync(key, cacheValue, option, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await cache.RemoveAsync(key, cancellationToken);
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        return await cache.GetStringAsync(key, cancellationToken);
    }

    public async Task SetStringAsync(string key, string value, TimeSpan lifeTime, CancellationToken cancellationToken = default)
    {
        await cache.SetStringAsync(key, value, new DistributedCacheEntryOptions() { AbsoluteExpirationRelativeToNow = lifeTime }, cancellationToken);
    }
}
