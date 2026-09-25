namespace Neo.Application.Features.Outbox.Implementation;

public sealed class OutboxExecutionJob(IOutboxDeliveryStore store, ISender sender, IPublisher publisher) : IOutboxExecutionJob
{
    public async Task Run(long outboxId, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(outboxId, cancellationToken)
            ?? throw new InvalidOperationException("Outbox message not found.");
        if (current.OutboxState == OutboxState.Processed) return;
        var token = Guid.NewGuid();
        var lease = await store.ClaimExecutionAsync(outboxId, token, TimeSpan.FromMinutes(5), cancellationToken);
        if (lease is null) throw new InvalidOperationException("Outbox execution is busy, not ready, or exhausted; inspect its state before retrying.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        await store.ExecuteClaimedAsync(outboxId, token, async (row, ct) =>
        {
            var type = Type.GetType(row.MessageType) ?? AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(row.MessageType)).FirstOrDefault(x => x is not null)
                ?? throw new InvalidOperationException("Outbox contract type is unavailable; deploy a compatible contract before replay.");
            if (!typeof(IOutboxMessage).IsAssignableFrom(type)) throw new InvalidOperationException("Type is not an outbox contract.");
            var message = row.MessageContent.FromJson(type) ?? throw new InvalidOperationException("Invalid outbox payload.");
            if (message is INotification notification) await publisher.Publish(notification, ct);
            else if (message is IRequest) await sender.Send(message, ct);
            else throw new InvalidOperationException("Outbox contract must implement IRequest or INotification.");
        }, timeout.Token);
    }
}
