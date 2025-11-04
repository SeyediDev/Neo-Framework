namespace Neo.Application.Features.Outbox.Dto;

/// <summary>
/// Standard outbox response containing job and state metadata.
/// </summary>
public record OutboxResponseId(
    /// <summary> Database identifier of the outbox record (used for QueryStatus). </summary>
    long OutboxId
);

/// <summary>
/// Standard outbox response containing job and state metadata.
/// </summary>
public record OutboxResponse(
    /// <summary> Database identifier of the outbox record (used for QueryStatus). </summary>
    long OutboxId,
    OutboxState OutboxState,
    /// <summary> Background job identifier (Hangfire, etc.). </summary>
    string? JobId = null,
    /// <summary> Idempotency key provided by client or system. </summary>
    string? IdempotencyKey = null
) : OutboxResponseId(OutboxId);