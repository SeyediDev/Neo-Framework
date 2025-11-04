namespace Neo.Application.Features.Outbox;

/// <summary>
/// Defines the contract for processing outbox messages, 
/// including enqueue operations with/without idempotency, 
/// querying message state, and updating status.
/// </summary>
/// <typeparam name="TOutboxMessage">The type of outbox message being processed.</typeparam>
public interface IOutboxMessageProcessor<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    /// <summary>
    /// Enqueue a message that contains its own idempotency key.
    /// </summary>
    /// <typeparam name="TIdempotenceMessage">Message type with idempotency key.</typeparam>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OutboxResponse"/> representing the enqueued message.</returns>
    [Telemetry("outbox", "enqueue_idempotent")]
    Task<OutboxResponse> EnqueueAsync<TIdempotenceMessage>(
        TIdempotenceMessage message, CancellationToken ct)
        where TIdempotenceMessage : IIdempotenceOutboxMessage, TOutboxMessage;

    /// <summary>
    /// Enqueue a message with an explicit idempotency key.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="idempotencyKey">The explicit idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OutboxResponse"/> representing the enqueued message.</returns>
    [Telemetry("outbox", "enqueue_with_key")]
    Task<OutboxResponse> EnqueueAsync(
        TOutboxMessage message, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Enqueue a message without idempotency.
    /// </summary>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="OutboxResponse"/> representing the enqueued message.</returns>
    [Telemetry("outbox", "enqueue_non_idempotent")]
    Task<OutboxResponse> EnqueueNonIdempotentAsync(
        TOutboxMessage message, CancellationToken ct);

    /// <summary>
    /// Retrieve the full outbox message response by its ID.
    /// </summary>
    /// <param name="outboxId">The outbox message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="OutboxResponse"/> if found, otherwise null.</returns>
    [Telemetry("outbox", "get_message")]
    Task<OutboxResponse?> GetMessageAsync(long outboxId, CancellationToken ct);

    /// <summary>
    /// Query only the status of an outbox message by its ID.
    /// </summary>
    /// <param name="outboxId">The outbox message identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageState"/> if found, otherwise null.</returns>
    [Telemetry("outbox", "get_status")]
    Task<MessageState?> GetStatusAsync(long outboxId, CancellationToken ct);

    /// <summary>
    /// Update the status and optional fields of an outbox message.
    /// </summary>
    /// <param name="outboxId">The outbox message identifier.</param>
    /// <param name="state">The new state of the outbox message.</param>
    /// <param name="jobId">Optional job identifier associated with the message.</param>
    /// <param name="content">Optional message content update.</param>
    /// <param name="ct">Cancellation token.</param>
    [Telemetry("outbox", "update_status")]
    Task UpdateStatusAsync(
        long outboxId, OutboxState state, string? jobId, string? content, CancellationToken ct);
}
