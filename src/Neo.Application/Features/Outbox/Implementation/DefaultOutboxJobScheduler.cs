using Neo.Application.Features.Queue;
using System.Text.Json;

namespace Neo.Application.Features.Outbox.Implementation;

public class DefaultOutboxJobScheduler(
    IJobExecuter jobExecuter
) : IOutboxJobScheduler
{
    private static string OutboxQueue => "outbox";
    public async Task<string?> ScheduleOnlineAsync(object message, CancellationToken ct)
    {
        if (message is IRequest request)
        {
            return await jobExecuter.EnqueueAsync<IJobCommand>((job, ct) => job.Run(request, ct), OutboxQueue, ct);
        }
        if (message is INotification notification)
        {
            return await jobExecuter.EnqueueAsync<IJobPublisher>((job, ct) => job.Run(notification, ct), OutboxQueue, ct);
        }
        throw new NotSupportedException($"Message type {message?.GetType().FullName} is not supported for scheduling.");
    }

    public async Task<string?> ScheduleOutboxMessageAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outboxMessage.MessageType) || string.IsNullOrWhiteSpace(outboxMessage.MessageContent))
            throw new ArgumentException("Invalid outbox message: missing type or content.");

        // 1. Resolve .NET type from stored string
        var messageType = Type.GetType(outboxMessage.MessageType, throwOnError: true)!;

        // 2. Deserialize JSON content to actual object
        var message = JsonSerializer.Deserialize(outboxMessage.MessageContent, messageType)
            ?? throw new InvalidOperationException($"Could not deserialize message of type {outboxMessage.MessageType}");

        // 3. Enqueue depending on type
        return await ScheduleOnlineAsync(message!, cancellationToken);
    }
}
