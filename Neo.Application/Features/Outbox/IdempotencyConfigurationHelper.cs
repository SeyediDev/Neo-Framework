using System.Reflection;

namespace Neo.Application.Features.Outbox;

/// <summary>
/// Utility class for reading idempotency configuration from attributes.
/// </summary>
public static class IdempotencyConfigurationHelper
{
    /// <summary>
    /// Gets the idempotency configuration for a specific outbox message type.
    /// </summary>
    /// <typeparam name="TOutboxMessage">The outbox message type</typeparam>
    /// <returns>IdempotencyAttribute if found, null otherwise</returns>
    public static IdempotencyAttribute? GetIdempotencyConfig<TOutboxMessage>()
        where TOutboxMessage : IOutboxMessage
    {
        return typeof(TOutboxMessage).GetCustomAttribute<IdempotencyAttribute>();
    }

    /// <summary>
    /// Gets the TTL for idempotency records of a specific outbox message type.
    /// </summary>
    /// <typeparam name="TOutboxMessage">The outbox message type</typeparam>
    /// <returns>TTL as TimeSpan, or null if unlimited</returns>
    public static TimeSpan? GetTtl<TOutboxMessage>()
        where TOutboxMessage : IOutboxMessage
    {
        var config = GetIdempotencyConfig<TOutboxMessage>();
        return config?.GetTtlAsTimeSpan();
    }

    /// <summary>
    /// Checks if idempotency records for a specific outbox message type have unlimited TTL.
    /// </summary>
    /// <typeparam name="TOutboxMessage">The outbox message type</typeparam>
    /// <returns>True if unlimited, false otherwise</returns>
    public static bool IsUnlimited<TOutboxMessage>()
        where TOutboxMessage : IOutboxMessage
    {
        var config = GetIdempotencyConfig<TOutboxMessage>();
        return config?.IsUnlimited ?? false; // Default to limited if no attribute
    }

    /// <summary>
    /// Gets the TTL for idempotency records of a specific outbox message type.
    /// Returns a default value if no attribute is found.
    /// </summary>
    /// <typeparam name="TOutboxMessage">The outbox message type</typeparam>
    /// <param name="defaultTtlDays">Default TTL in days if no attribute is found</param>
    /// <returns>TTL as TimeSpan</returns>
    public static TimeSpan GetTtlOrDefault<TOutboxMessage>(int defaultTtlDays = 30)
        where TOutboxMessage : IOutboxMessage
    {
        var config = GetIdempotencyConfig<TOutboxMessage>();
        if (config != null && !config.IsUnlimited)
        {
            return TimeSpan.FromDays(config.TtlDays);
        }
        return TimeSpan.FromDays(defaultTtlDays);
    }
}
