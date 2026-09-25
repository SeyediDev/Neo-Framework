namespace Neo.Domain.Features.Cache;

/// <summary>Atomically adds a value only when the key is absent. Scope follows the provider (memory is process-local).</summary>
public interface IAtomicCacheService : ICacheService
{
    Task<bool> TryAddAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
