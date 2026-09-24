using System.Collections.Concurrent;
using MassTransit;

namespace Neo.Samples.Messaging;

public sealed record OrderObservation(int Attempts, bool Completed);

/// <summary>Process-local teaching state, not a durable inbox or production exactly-once guarantee.</summary>
public sealed class DemoObservations
{
    private readonly ConcurrentDictionary<string, OrderObservation> entries = new();
    public IReadOnlyDictionary<string, OrderObservation> Snapshot() => new Dictionary<string, OrderObservation>(entries);
    public void Record(string consumer, OrderSubmitted message)
    {
        var key = $"{consumer}:{message.EventId}";
        // The configured concurrency limit is one per endpoint. This example has one worker process.
        var prior = entries.GetOrAdd(key, new OrderObservation(0, false));
        if (prior.Completed) return;
        var next = prior with { Attempts = prior.Attempts + 1 };
        entries[key] = next;
        if (consumer == "fulfillment" && message.FailPermanently)
            throw new InvalidOperationException("Demonstration of a non-retryable business failure.");
        if (consumer == "fulfillment" && message.FailOnce && next.Attempts == 1)
            throw new TimeoutException("Demonstration of a transient dependency timeout.");
        // A real effect needs an inbox/business unique key in the same transaction as the effect.
        entries[key] = next with { Completed = true };
    }
}

public sealed class FulfillmentConsumer(DemoObservations observations, ILogger<FulfillmentConsumer> logger) : IConsumer<OrderSubmitted>
{
    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        observations.Record("fulfillment", context.Message);
        logger.LogInformation("Observed fulfillment event {EventId}, order {OrderId}", context.Message.EventId, context.Message.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class AuditConsumer(DemoObservations observations) : IConsumer<OrderSubmitted>
{
    public Task Consume(ConsumeContext<OrderSubmitted> context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        observations.Record("audit", context.Message);
        return Task.CompletedTask;
    }
}

public sealed class FulfillmentDefinition : ConsumerDefinition<FulfillmentConsumer>
{
    public FulfillmentDefinition() => ConcurrentMessageLimit = 1;
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<FulfillmentConsumer> consumer, IRegistrationContext context)
    {
        endpoint.UseMessageRetry(retry =>
        {
            retry.Handle<TimeoutException>();
            retry.Intervals(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        });
        // Buffers outgoing consumer messages until Consume succeeds. NOT a durable database outbox.
        endpoint.UseInMemoryOutbox(context);
    }
}

public sealed class AuditDefinition : ConsumerDefinition<AuditConsumer>
{
    public AuditDefinition() => ConcurrentMessageLimit = 1;
}
