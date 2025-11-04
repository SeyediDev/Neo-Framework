namespace Neo.Application.Features.Outbox;

public interface IOutboxJobScheduler
{
    [Telemetry] // 👈 Attribute روی متد
    Task<string?> ScheduleOnlineAsync(object message, CancellationToken ct);
    [Telemetry] // 👈 Attribute روی متد
    Task<string?> ScheduleOutboxMessageAsync(OutboxMessage outboxMessage, CancellationToken token);
}
