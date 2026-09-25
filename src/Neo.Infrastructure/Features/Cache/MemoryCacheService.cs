using Neo.Common.Extensions;
using Neo.Domain.Features.Cache;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Runtime.CompilerServices;

namespace Neo.Infrastructure.Features.Cache;

public class MemoryCacheService(IMemoryCache memoryCache) : IAtomicCacheService
{
    private static readonly ConditionalWeakTable<IMemoryCache, object> AtomicGates = new();
    public Task<bool> TryAddAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (AtomicGates.GetValue(memoryCache, _ => new object()))
        {
            if (memoryCache.TryGetValue(key, out _)) return Task.FromResult(false);
            memoryCache.Set(key, value.ToJson(), lifetime);
            return Task.FromResult(true);
        }
    }
    public T? Get<T>(string key)
    {
        if( memoryCache.TryGetValue(key, out string? cachedValue) && cachedValue!=null )
        {
            return cachedValue.FromJson<T>();
        }
        return default;
    }

    public void Set<T>(string key, T? value)
    {
        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(10)); // مقدار پیش‌فرض

        memoryCache.Set(key, value?.ToJson(), cacheEntryOptions);
    }

    public void Set<T>(string key, T? value, TimeSpan lifeTime)
    {
        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(lifeTime);

        memoryCache.Set(key, value?.ToJson(), cacheEntryOptions);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // IMemoryCache همگام است، اما می‌توانیم برای حفظ سازگاری با اینترفیس از Task برگردیم
        return await Task.FromResult(Get<T>(key));
    }

    public async Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions option, CancellationToken cancellationToken = default)
    {
        var cacheEntryOptions = new MemoryCacheEntryOptions();

        if (option.AbsoluteExpiration.HasValue)
            cacheEntryOptions.SetAbsoluteExpiration(option.AbsoluteExpiration.Value);
        else if (option.AbsoluteExpirationRelativeToNow.HasValue)
            cacheEntryOptions.SetAbsoluteExpiration(option.AbsoluteExpirationRelativeToNow.Value);
        else if (option.SlidingExpiration.HasValue)
            cacheEntryOptions.SetSlidingExpiration(option.SlidingExpiration.Value);

        memoryCache.Set(key, value.ToJson(), cacheEntryOptions);

        await Task.CompletedTask;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        memoryCache.Remove(key);
        await Task.CompletedTask;
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            memoryCache.TryGetValue(key, out string? value) ? value : null);
    }

    public async Task SetStringAsync(string key, string value, TimeSpan lifeTime, CancellationToken cancellationToken = default)
    {
        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(lifeTime);

        memoryCache.Set(key, value, cacheEntryOptions);

        await Task.CompletedTask;
    }
}
