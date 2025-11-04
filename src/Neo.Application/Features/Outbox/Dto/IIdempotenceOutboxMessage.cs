namespace Neo.Application.Features.Outbox.Dto;

/// <summary>
/// Optional contract for requests that require idempotency.
/// </summary>
public interface IIdempotenceOutboxMessage : IOutboxMessage
{
    string? IdempotencyKey { get; }
}
