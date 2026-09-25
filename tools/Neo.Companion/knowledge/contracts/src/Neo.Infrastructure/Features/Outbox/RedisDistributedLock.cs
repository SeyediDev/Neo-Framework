using System.Collections.Concurrent;
using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Neo.Infrastructure.Features.Outbox;

/// <summary>Redis owner-token lease. Timeout is the lease lifetime, not acquisition wait time.
/// Actions must finish within that lifetime. This is not a fencing token or an exactly-once guarantee.</summary>
public class RedisDistributedLock(IConnectionMultiplexer connection, ILogger<RedisDistributedLock> logger) : IDistributedLock
{
    private readonly ConcurrentDictionary<string, string> owners = new();
    private const string Prefix = "neo:outbox:lease:";
    public async Task<bool> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        ct.ThrowIfCancellationRequested();
        var owner = Guid.NewGuid().ToString("N");
        if (!owners.TryAdd(key, owner)) return false;
        try
        {
            var acquired = await connection.GetDatabase().StringSetAsync(Prefix + key, owner, timeout, When.NotExists);
            if (!acquired) owners.TryRemove(key, out _);
            if (ct.IsCancellationRequested)
            {
                if (acquired) await ReleaseAsync(key, CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }
            return acquired;
        }
        catch { owners.TryRemove(key, out _); throw; }
    }
    public async Task ReleaseAsync(string key, CancellationToken ct = default)
    {
        // Cleanup must be attempted even when the caller's operation was cancelled.
        if (!owners.TryGetValue(key, out var owner)) return;
        await connection.GetDatabase().ScriptEvaluateAsync(
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end",
            [Prefix + key], [owner]);
        ((ICollection<KeyValuePair<string, string>>)owners).Remove(new(key, owner));
    }
    public async Task<bool> ExecuteWithLockAsync(string key, TimeSpan timeout, Func<Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!await TryAcquireAsync(key, timeout, ct)) return false;
        try { ct.ThrowIfCancellationRequested(); await action(); return true; }
        finally
        {
            try { await ReleaseAsync(key, CancellationToken.None); }
            catch (Exception error) { logger.LogError(error, "Unable to release Redis lease {Key}; it will expire", key); }
        }
    }
}

