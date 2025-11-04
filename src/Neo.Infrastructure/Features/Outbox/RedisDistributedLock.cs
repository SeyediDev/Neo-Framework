using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Features.Outbox;

/// <summary>
/// Redis-based implementation of distributed lock for preventing concurrent execution.
/// Uses Redis with expiration to ensure locks are automatically released.
/// </summary>
public class RedisDistributedLock : IDistributedLock
{
    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<RedisDistributedLock> _logger;
    private const string LockPrefix = "distributed_lock:";
    private const int DefaultLockExpirationMinutes = 10;

    public RedisDistributedLock(IDistributedCache distributedCache, ILogger<RedisDistributedLock> logger)
    {
        _distributedCache = distributedCache;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockKey = $"{LockPrefix}{key}";
        var lockValue = Guid.NewGuid().ToString();
        var expiration = TimeSpan.FromMinutes(DefaultLockExpirationMinutes);

        try
        {
            // Try to set the lock with expiration
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            };

            await _distributedCache.SetStringAsync(lockKey, lockValue, options, ct);
            
            // Verify we actually got the lock (in case of race condition)
            var existingValue = await _distributedCache.GetStringAsync(lockKey, ct);
            if (existingValue == lockValue)
            {
                _logger.LogDebug("Successfully acquired distributed lock for key: {Key}", key);
                return true;
            }

            _logger.LogDebug("Failed to acquire distributed lock for key: {Key} - already held by another instance", key);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring distributed lock for key: {Key}", key);
            return false;
        }
    }

    public async Task ReleaseAsync(string key, CancellationToken ct = default)
    {
        var lockKey = $"{LockPrefix}{key}";

        try
        {
            await _distributedCache.RemoveAsync(lockKey, ct);
            _logger.LogDebug("Released distributed lock for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing distributed lock for key: {Key}", key);
        }
    }

    public async Task<bool> ExecuteWithLockAsync(string key, TimeSpan timeout, Func<Task> action, CancellationToken ct = default)
    {
        var lockAcquired = await TryAcquireAsync(key, timeout, ct);
        
        if (!lockAcquired)
        {
            _logger.LogInformation("Could not acquire distributed lock for key: {Key} - skipping execution", key);
            return false;
        }

        try
        {
            _logger.LogInformation("Executing action with distributed lock for key: {Key}", key);
            await action();
            return true;
        }
        finally
        {
            await ReleaseAsync(key, ct);
        }
    }
}
