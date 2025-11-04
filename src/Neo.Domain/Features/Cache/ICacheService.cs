using Microsoft.Extensions.Caching.Distributed;

namespace Neo.Domain.Features.Cache;

public interface ICacheService
{
    T? Get<T>(string key);
    void Set<T>(string key, T? value);
    void Set<T>(string key, T? value, TimeSpan lifeTime);

    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions option, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);
    Task SetStringAsync(string key, string value, TimeSpan timeSpan, CancellationToken cancellationToken = default);
}
