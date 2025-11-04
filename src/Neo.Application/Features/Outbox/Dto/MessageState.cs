namespace Neo.Application.Features.Outbox.Dto;

public record MessageState(OutboxState OutboxState, string? JobId);