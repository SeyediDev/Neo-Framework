namespace Neo.Application.Features.Outbox;

/// <summary>
/// Attribute to configure idempotency settings for outbox messages.
/// Can be applied to classes implementing IOutboxMessage.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class IdempotencyAttribute : Attribute
{
    /// <summary>
    /// Time-to-live for idempotency records in days.
    /// If -1, records will never expire (unlimited).
    /// </summary>
    public int TtlDays { get; set; }

    /// <summary>
    /// Initializes a new instance of IdempotencyAttribute.
    /// </summary>
    /// <param name="ttlDays">TTL in days. If -1, records never expire.</param>
    public IdempotencyAttribute(int ttlDays = 30)
    {
        TtlDays = ttlDays;
    }

    /// <summary>
    /// Gets the TTL as TimeSpan. Returns null if TtlDays is -1 (unlimited).
    /// </summary>
    public TimeSpan? GetTtlAsTimeSpan()
    {
        return TtlDays == -1 ? null : TimeSpan.FromDays(TtlDays);
    }

    /// <summary>
    /// Checks if the TTL is unlimited (-1).
    /// </summary>
    public bool IsUnlimited => TtlDays == -1;
}
