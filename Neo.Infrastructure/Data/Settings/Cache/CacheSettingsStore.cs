using Neo.Domain.Features;
using Neo.Domain.Features.Cache;

namespace Neo.Infrastructure.Data.Settings.Cache;

public class CacheSettingsStore(ICacheService cacheService) : ISettingService
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        return await cacheService.GetAsync<string>(key, cancellationToken);
    }

    public async Task<T?> GetValueAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        return await cacheService.GetAsync<T>(key, cancellationToken);
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await cacheService.SetAsync(key, value, new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.MaxValue }, cancellationToken);
    }

    public async Task SetValueAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await cacheService.SetAsync(key, value, new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.MaxValue }, cancellationToken);
    }
}