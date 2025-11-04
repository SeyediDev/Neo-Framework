namespace Neo.Application.Features.Outbox.Examples;

/// <summary>
/// Example of how to use IdempotencyAttribute on outbox message classes.
/// </summary>

// Example 1: Default TTL (30 days)
[Idempotency] // Uses default 30 days TTL
public class DefaultTtlOutboxMessage : IOutboxMessage
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}

// Example 2: Custom TTL (7 days)
[Idempotency(7)]
public class ShortTtlOutboxMessage : IOutboxMessage
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}

// Example 3: Unlimited TTL (never expires)
[Idempotency(-1)]
public class UnlimitedTtlOutboxMessage : IOutboxMessage
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}

// Example 4: Long TTL (90 days)
[Idempotency(90)]
public class LongTtlOutboxMessage : IOutboxMessage
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}

// Example 5: No attribute (uses default 30 days)
public class NoAttributeOutboxMessage : IOutboxMessage
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);
}
