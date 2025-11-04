namespace Neo.Application.Features.Outbox;

/// <summary>
/// Interface for distributed lock mechanism to prevent concurrent execution across multiple instances.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire a distributed lock with the specified key and timeout.
    /// </summary>
    /// <param name="key">The lock key</param>
    /// <param name="timeout">The timeout duration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if lock was acquired, false otherwise</returns>
    Task<bool> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Releases a distributed lock with the specified key.
    /// </summary>
    /// <param name="key">The lock key</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Task representing the release operation</returns>
    Task ReleaseAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Executes an action with a distributed lock, automatically releasing the lock when done.
    /// </summary>
    /// <param name="key">The lock key</param>
    /// <param name="timeout">The timeout duration</param>
    /// <param name="action">The action to execute</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if lock was acquired and action executed, false if lock could not be acquired</returns>
    Task<bool> ExecuteWithLockAsync(string key, TimeSpan timeout, Func<Task> action, CancellationToken ct = default);
}
