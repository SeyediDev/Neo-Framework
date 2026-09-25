using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Neo.Domain.Entities.Base;
using Neo.Infrastructure.Features.Messaging;
using Neo.Samples.Messaging;

namespace Neo.Samples.DurableMessaging;

public sealed class DurableContext(DbContextOptions<DurableContext> options) : DbContext(options)
{
    public DbSet<DemoOrder> Orders => Set<DemoOrder>();
    public DbSet<InventoryEffect> Inventory => Set<InventoryEffect>();
    public DbSet<PaymentEffect> Payments => Set<PaymentEffect>();
    public DbSet<OrderSagaState> Sagas => Set<OrderSagaState>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DemoOrder>().HasKey(x => x.Id);
        model.Entity<InventoryEffect>().HasKey(x => x.OrderId);
        model.Entity<PaymentEffect>().HasKey(x => x.OrderId);
        model.Entity<OrderSagaState>().HasKey(x => x.CorrelationId);
        model.Entity<OrderSagaState>().Property(x => x.CurrentState).HasMaxLength(64);
        model.AddNeoMessagingOutbox();
    }
}
public sealed class DemoOrder : BaseEntity<Guid>
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public static DemoOrder Create(StartOrder input)
    {
        if (input.OrderId == Guid.Empty) throw new ArgumentException("OrderId required");
        var order = new DemoOrder { Id = input.OrderId };
        order.AddDomainEvent(new OrderPlaced(input));
        return order;
    }
}
public sealed class OrderPlaced(StartOrder input) : BaseEvent { public StartOrder Input { get; } = input; }
public sealed class OrderPlacedHandler(IPublishEndpoint publisher) : INotificationHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken ct) => publisher.Publish(notification.Input, ct);
}
public sealed class InventoryEffect
{
    public Guid OrderId { get; set; }
    public bool Reserved { get; set; }
    public int ReserveEffects { get; set; }
    public int ReleaseEffects { get; set; }
}
public sealed class PaymentEffect
{
    public Guid OrderId { get; set; }
    public bool Succeeded { get; set; }
    public int Effects { get; set; }
}
