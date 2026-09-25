using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.Infrastructure.Data.Interceptors;
using Neo.Infrastructure.Features.Messaging;
using Neo.Samples.Messaging;

namespace Neo.Samples.DurableMessaging;

public static class DurableProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5089");
        var connection = builder.Configuration.GetConnectionString("Demo") ?? throw new InvalidOperationException("Demo database required");
        builder.Services.AddMediatR(x => x.RegisterServicesFromAssemblyContaining<OrderPlacedHandler>());
        builder.Services.AddScoped<DispatchDomainEventsInterceptor>();
        builder.Services.AddDbContext<DurableContext>((services, options) => options.UseSqlServer(connection)
            .AddInterceptors(services.GetRequiredService<DispatchDomainEventsInterceptor>()));
        builder.Services.AddSingleton<SagaObservations>();
        builder.Services.AddSingleton<FailureInjection>();
        builder.Services.AddHealthChecks();
        var names = new KebabCaseEndpointNameFormatter(builder.Configuration["RabbitMq:EndpointPrefix"] ?? "neo-durable", false);
        builder.Services.AddNeoRabbitMq(builder.Configuration, bus =>
        {
            // Retry must wrap the outbox so every retry gets a fresh EF transaction.
            bus.AddConfigureEndpointsCallback((_, _, endpoint) =>
            {
                endpoint.ConcurrentMessageLimit = 1;
                endpoint.UseMessageRetry(retry =>
                {
                    retry.Handle<DbUpdateConcurrencyException>();
                    retry.Handle<SqlException>(e => e.Number is 1205 or 2601 or 2627);
                    retry.Intervals(100, 300, 1000);
                });
            });
            bus.AddNeoSqlServerOutbox<DurableContext>(!builder.Configuration.GetValue<bool>("DeferDelivery"));
            bus.AddConsumer<DurableReserveConsumer>().Endpoint(x => x.Name = names.Consumer<ReserveInventoryConsumer>());
            bus.AddConsumer<DurableChargeConsumer>().Endpoint(x => x.Name = names.Consumer<ChargePaymentConsumer>());
            bus.AddConsumer<DurableReleaseConsumer, DurableReleaseDefinition>().Endpoint(x => x.Name = names.Consumer<ReleaseInventoryConsumer>());
            bus.AddSagaStateMachine<OrderSaga, OrderSagaState>().EntityFrameworkRepository(repository =>
            {
                repository.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                repository.ExistingDbContext<DurableContext>();
                repository.UseSqlServer();
            });
        });
        var app = builder.Build();
        if (builder.Configuration.GetValue<bool>("InitializeDatabase"))
        {
            if (!new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("NeoDurableDemo", StringComparison.Ordinal))
                throw new InvalidOperationException("Demo initialization only allows a NeoDurableDemo database. Use migrations for your application.");
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DurableContext>().Database.EnsureCreatedAsync();
        }
        app.MapHealthChecks("/health/ready");
        app.MapPost("/orders", async (StartOrder input, bool? rollback, DurableContext db, CancellationToken ct) =>
        {
            if (input.OrderId == Guid.Empty || input.ReleaseFailures is < 0 or > 10) return Results.BadRequest();
            if (await db.Orders.AnyAsync(x => x.Id == input.OrderId, ct)) return Results.Conflict();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.Orders.Add(DemoOrder.Create(input));
            await db.SaveChangesAsync(ct);
            if (rollback == true) { await transaction.RollbackAsync(ct); return Results.Ok(new { rolledBack = true }); }
            await transaction.CommitAsync(ct);
            return Results.Accepted(value: new { input.OrderId });
        });
        app.MapGet("/state/{id:guid}", async (Guid id, DurableContext db, CancellationToken ct) => new
        {
            orderExists = await db.Orders.AnyAsync(x => x.Id == id, ct),
            saga = await db.Sagas.AsNoTracking().SingleOrDefaultAsync(x => x.CorrelationId == id, ct),
            inventory = await db.Inventory.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id, ct),
            payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id, ct)
        });
        app.MapGet("/outbox", async (DurableContext db, CancellationToken ct) => new { pendingMessages = await db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync(ct) });
        app.MapPost("/orders/{id:guid}/retry-compensation", async (Guid id, DurableContext db, IPublishEndpoint publisher, CancellationToken ct) =>
        {
            var saga = await db.Sagas.AsNoTracking().SingleOrDefaultAsync(x => x.CorrelationId == id, ct);
            if (saga is null || saga.CurrentState != "ManualReview" || !saga.CompensationRequired) return Results.Conflict();
            await publisher.Publish(new RetryOrderCompensation(id), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted();
        });
        await app.RunAsync();
    }
}
