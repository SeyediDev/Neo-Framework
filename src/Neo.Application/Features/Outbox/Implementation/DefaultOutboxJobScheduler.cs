using Neo.Application.Features.Queue;

namespace Neo.Application.Features.Outbox.Implementation;

public class DefaultOutboxJobScheduler(
    IJobExecuter jobExecuter) : IOutboxJobScheduler
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
	
	public string? ScheduleOnline(object message)
	{
		if (message is IRequest request)
		{
			return jobExecuter.Enqueue<IJobCommand>(job => job.Run(request, CancellationToken.None), OutboxQueue);
		}
		if (message is INotification notification)
		{
			return jobExecuter.Enqueue<IJobPublisher>(job => job.Run(notification, CancellationToken.None), OutboxQueue);
		}
		throw new NotSupportedException($"Message type {message?.GetType().FullName} is not supported for scheduling.");
	}

	public async Task<string?> ScheduleOutboxMessageAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outboxMessage.MessageType) || string.IsNullOrWhiteSpace(outboxMessage.MessageContent))
            throw new ArgumentException("Invalid outbox message: missing type or content.");

        try
        {
            // 1. Resolve .NET type from stored string
            Type messageType = ResolveMessageType(outboxMessage.MessageType)!;

            // 2. Deserialize JSON content to actual object
            var message = outboxMessage.MessageContent.FromJson(messageType)
                ?? throw new InvalidOperationException($"Could not deserialize message of type {outboxMessage.MessageType}");

            // 3. Enqueue depending on type
            return await ScheduleOnlineAsync(message!, cancellationToken);
        }
        catch (Exception exp)
        {
            throw new InvalidOperationException($"Could not find type {outboxMessage.MessageType} {exp.Message}");
        }
    }
	
	public string? ScheduleOutboxMessage(OutboxMessage outboxMessage)
	{
		if (string.IsNullOrWhiteSpace(outboxMessage.MessageType) || string.IsNullOrWhiteSpace(outboxMessage.MessageContent))
			throw new ArgumentException("Invalid outbox message: missing type or content.");

		try
		{
			// 1. Resolve .NET type from stored string
			Type messageType = ResolveMessageType(outboxMessage.MessageType)!;

			// 2. Deserialize JSON content to actual object
			var message = outboxMessage.MessageContent.FromJson(messageType)
				?? throw new InvalidOperationException($"Could not deserialize message of type {outboxMessage.MessageType}");

			// 3. Enqueue depending on type
			return ScheduleOnline(message!);
		}
		catch (Exception exp)
		{
			throw new InvalidOperationException($"Could not find type {outboxMessage.MessageType} {exp.Message}");
		}
	}

	private static Type? ResolveMessageType(string typeFullName)
	{
		// اول سعی می‌کند از متد نرمال استفاده کند
		var type = Type.GetType(typeFullName);
		if (type != null)
			return type;

		// اگر پیدا نشد، بین تمام اسمبلی‌های Load شده جستجو می‌کنیم
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			type = asm.GetType(typeFullName);
			if (type != null)
				return type;
		}

		throw new InvalidOperationException($"Could not resolve type '{typeFullName}' in any loaded assembly.");
	}
}