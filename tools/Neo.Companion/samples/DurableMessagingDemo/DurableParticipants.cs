using System.Collections.Concurrent;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Neo.Samples.Messaging;

namespace Neo.Samples.DurableMessaging;

// Only fault injection is process-local. Business effects and saga state are persisted.
public sealed class FailureInjection
{
    private readonly ConcurrentDictionary<Guid,int> attempts = new();
    public void Release(Guid id, int failures)
    {
        if (attempts.AddOrUpdate(id, 1, (_, n) => n + 1) <= failures)
            throw new TimeoutException("Simulated release timeout");
    }
}
public sealed class DurableReserveConsumer(DurableContext db) : IConsumer<ReserveInventory>
{
    public async Task Consume(ConsumeContext<ReserveInventory> context)
    {
        if (!await db.Inventory.AnyAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken))
            db.Inventory.Add(new() { OrderId = context.Message.OrderId, Reserved = true, ReserveEffects = 1 });
        await context.Publish(new InventoryReserved(context.Message.OrderId), context.CancellationToken);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
public sealed class DurableChargeConsumer(DurableContext db) : IConsumer<ChargePayment>
{
    public async Task Consume(ConsumeContext<ChargePayment> context)
    {
        if (context.Message.Timeout) throw new TimeoutException("Payment outcome unknown; reconciliation required.");
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);
        if (payment is null)
        {
            payment = new() { OrderId = context.Message.OrderId, Succeeded = !context.Message.Decline, Effects = context.Message.Decline ? 0 : 1 };
            db.Payments.Add(payment);
        }
        if (payment.Succeeded) await context.Publish(new PaymentSucceeded(payment.OrderId), context.CancellationToken);
        else await context.Publish(new PaymentDeclined(payment.OrderId), context.CancellationToken);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
public sealed class DurableReleaseConsumer(DurableContext db, FailureInjection failures) : IConsumer<ReleaseInventory>
{
    public async Task Consume(ConsumeContext<ReleaseInventory> context)
    {
        var inventory = await db.Inventory.SingleAsync(x => x.OrderId == context.Message.OrderId, context.CancellationToken);
        if (inventory.Reserved)
        {
            failures.Release(context.Message.OrderId, context.Message.Failures);
            inventory.Reserved = false; inventory.ReleaseEffects++;
        }
        await context.Publish(new InventoryReleased(inventory.OrderId), context.CancellationToken);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
public sealed class DurableReleaseDefinition : ConsumerDefinition<DurableReleaseConsumer>
{
    public DurableReleaseDefinition() => ConcurrentMessageLimit = 1;
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint, IConsumerConfigurator<DurableReleaseConsumer> consumer, IRegistrationContext context)
        => endpoint.UseMessageRetry(retry => { retry.Handle<TimeoutException>(); retry.Immediate(2); });
}
