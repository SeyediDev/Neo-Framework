using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Features.Outbox;

/// <summary>
/// Memory-based implementation of distributed lock for preventing concurrent execution within a single instance.
/// Note: This implementation only works within a single application instance and does not provide
/// true distributed locking across multiple instances.
/// </summary>
public class MemoryDistributedLock : IDistributedLock
{
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<MemoryDistributedLock> _logger;
    private const string LockPrefix = "distributed_lock:";
    private const int DefaultLockExpirationMinutes = 10;

    public MemoryDistributedLock(IMemoryCache memoryCache, ILogger<MemoryDistributedLock> logger)
    {
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public Task<bool> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockKey = $"{LockPrefix}{key}";
        var lockValue = Guid.NewGuid().ToString();
        var expiration = TimeSpan.FromMinutes(DefaultLockExpirationMinutes);

        try
        {
            // Try to acquire the lock using TryGetValue and Set
            if (_memoryCache.TryGetValue(lockKey, out _))
            {
                _logger.LogDebug("Failed to acquire memory distributed lock for key: {Key} - already held", key);
                return Task.FromResult(false);
            }

            // Set the lock with expiration
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            };

            _memoryCache.Set(lockKey, lockValue, options);
            
            // Verify we actually got the lock
            if (_memoryCache.TryGetValue(lockKey, out var existingValue) && existingValue?.ToString() == lockValue)
            {
                _logger.LogDebug("Successfully acquired memory distributed lock for key: {Key}", key);
                return Task.FromResult(true);
            }

            _logger.LogDebug("Failed to acquire memory distributed lock for key: {Key} - race condition", key);
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring memory distributed lock for key: {Key}", key);
            return Task.FromResult(false);
        }
    }

    public Task ReleaseAsync(string key, CancellationToken ct = default)
    {
        var lockKey = $"{LockPrefix}{key}";

        try
        {
            _memoryCache.Remove(lockKey);
            _logger.LogDebug("Released memory distributed lock for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error releasing memory distributed lock for key: {Key}", key);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> ExecuteWithLockAsync(string key, TimeSpan timeout, Func<Task> action, CancellationToken ct = default)
    {
        var lockAcquired = await TryAcquireAsync(key, timeout, ct);
        
        if (!lockAcquired)
        {
            _logger.LogInformation("Could not acquire memory distributed lock for key: {Key} - skipping execution", key);
            return false;
        }

        try
        {
            _logger.LogInformation("Executing action with memory distributed lock for key: {Key}", key);
            await action();
            return true;
        }
        finally
        {
            await ReleaseAsync(key, ct);
        }
    }
}
