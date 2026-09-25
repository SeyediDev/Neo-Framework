using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Neo.Infrastructure.Features.Messaging;

public static class SqlServerOutboxExtensions
{
    /// <summary>One bus and one scoped business DbContext. Commit business changes and messages together.
    /// Consumers using this helper must use the same DbContext for their transactional effects.</summary>
    public static void AddNeoSqlServerOutbox<TContext>(this IBusRegistrationConfigurator bus,
        bool enableDelivery = true, bool enableConsumerOutbox = true) where TContext : DbContext
    {
        bus.AddEntityFrameworkOutbox<TContext>(options =>
        {
            options.UseSqlServer();
            options.QueryDelay = TimeSpan.FromSeconds(1);
            options.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            options.UseBusOutbox(delivery => { if (!enableDelivery) delivery.DisableDeliveryService(); });
        });
        if (enableConsumerOutbox)
            bus.AddConfigureEndpointsCallback((context, _, endpoint) => endpoint.UseEntityFrameworkOutbox<TContext>(context));
    }

    public static void AddNeoMessagingOutbox(this ModelBuilder model)
    {
        model.AddInboxStateEntity();
        model.AddOutboxMessageEntity();
        model.AddOutboxStateEntity();
    }
}
