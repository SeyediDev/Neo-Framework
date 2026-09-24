using System.Collections.Concurrent;
using MassTransit;

namespace Neo.Samples.Messaging;

public sealed record ReservationObservation(bool Reserved, int ReserveEffects, int ReleaseEffects, int ReleaseAttempts);
public sealed record PaymentObservation(bool Succeeded, int Effects);

// Simulated local services: lock+memory demonstrates operation idempotency, not distributed durability.
public sealed class DemoOrderServices
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, ReservationObservation> inventory = new();
    private readonly ConcurrentDictionary<Guid, PaymentObservation> payments = new();
    public object Snapshot() => new { inventory = new Dictionary<Guid, ReservationObservation>(inventory), payments = new Dictionary<Guid, PaymentObservation>(payments) };
    public ReservationObservation? Reservation(Guid id) => inventory.GetValueOrDefault(id);
    public PaymentObservation? Payment(Guid id) => payments.GetValueOrDefault(id);
    public void Reserve(Guid id)
    {
        lock (gate) inventory.TryAdd(id, new(true, 1, 0, 0));
    }
    public bool Charge(Guid id, bool decline)
    {
        lock (gate) return payments.GetOrAdd(id, _ => new(!decline, decline ? 0 : 1)).Succeeded;
    }
    public void Release(Guid id, int failures)
    {
        lock (gate)
        {
            var current = inventory.GetValueOrDefault(id) ?? throw new InvalidOperationException("Unknown reservation; manual reconciliation required.");
            if (!current.Reserved) return;
            var next = current with { ReleaseAttempts = current.ReleaseAttempts + 1 };
            inventory[id] = next;
            if (next.ReleaseAttempts <= failures) throw new TimeoutException("Simulated inventory release timeout");
            inventory[id] = next with { Reserved = false, ReleaseEffects = next.ReleaseEffects + 1 };
        }
    }
}

public sealed class ReserveInventoryConsumer(DemoOrderServices services) : IConsumer<ReserveInventory>
{
    public async Task Consume(ConsumeContext<ReserveInventory> context)
    {
        services.Reserve(context.Message.OrderId);
        await context.Publish(new InventoryReserved(context.Message.OrderId), context.CancellationToken);
    }
}
public sealed class ChargePaymentConsumer(DemoOrderServices services) : IConsumer<ChargePayment>
{
    public async Task Consume(ConsumeContext<ChargePayment> context)
    {
        if (context.Message.Timeout) throw new TimeoutException("Payment outcome unknown; demonstration requires manual reconciliation.");
        if (services.Charge(context.Message.OrderId, context.Message.Decline))
            await context.Publish(new PaymentSucceeded(context.Message.OrderId), context.CancellationToken);
        else
            await context.Publish(new PaymentDeclined(context.Message.OrderId), context.CancellationToken);
    }
}
public sealed class ReleaseInventoryConsumer(DemoOrderServices services) : IConsumer<ReleaseInventory>
{
    public async Task Consume(ConsumeContext<ReleaseInventory> context)
    {
        services.Release(context.Message.OrderId, context.Message.Failures);
        await context.Publish(new InventoryReleased(context.Message.OrderId), context.CancellationToken);
    }
}
public sealed class ReleaseInventoryDefinition : ConsumerDefinition<ReleaseInventoryConsumer>
{
    public ReleaseInventoryDefinition() => ConcurrentMessageLimit = 1;
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<ReleaseInventoryConsumer> consumer, IRegistrationContext context)
    {
        endpoint.UseMessageRetry(retry => { retry.Handle<TimeoutException>(); retry.Immediate(2); });
        endpoint.UseInMemoryOutbox(context);
    }
}

public static class OrderSagaRegistration
{
    public static void AddDemoOrderSaga(this IBusRegistrationConfigurator bus)
    {
        bus.AddSagaStateMachine<OrderSaga, OrderSagaState, OrderSagaDefinition>().InMemoryRepository();
        bus.AddConsumer<ReserveInventoryConsumer>();
        bus.AddConsumer<ChargePaymentConsumer>();
        bus.AddConsumer<ReleaseInventoryConsumer, ReleaseInventoryDefinition>();
    }
}
