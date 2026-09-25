using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Features.Outbox;

/// <summary>Owner-token lease shared by instances using the same IMemoryCache. Process-local only.</summary>
public class MemoryDistributedLock(IMemoryCache cache, ILogger<MemoryDistributedLock> logger) : IDistributedLock
{
    private static readonly ConditionalWeakTable<IMemoryCache, object> Gates = new();
    private readonly ConcurrentDictionary<string, string> owners = new();
    private const string Prefix = "neo:outbox:lease:";
    public Task<bool> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ct.ThrowIfCancellationRequested();
        lock (Gates.GetValue(cache, _ => new object()))
        {
            if (cache.TryGetValue(Prefix + key, out _) || owners.ContainsKey(key)) return Task.FromResult(false);
            var owner = Guid.NewGuid().ToString("N");
            owners[key] = owner;
            cache.Set(Prefix + key, owner, timeout);
            return Task.FromResult(true);
        }
    }
    public Task ReleaseAsync(string key, CancellationToken ct = default)
    {
        lock (Gates.GetValue(cache, _ => new object()))
        {
            if (owners.TryRemove(key, out var owner) && cache.TryGetValue(Prefix + key, out string? actual) && actual == owner)
                cache.Remove(Prefix + key);
        }
        return Task.CompletedTask;
    }
    public async Task<bool> ExecuteWithLockAsync(string key, TimeSpan timeout, Func<Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!await TryAcquireAsync(key, timeout, ct)) return false;
        try { ct.ThrowIfCancellationRequested(); await action(); return true; }
        finally
        {
            await ReleaseAsync(key, CancellationToken.None);
            logger.LogDebug("Released process-local lease {Key}", key);
        }
    }
}
