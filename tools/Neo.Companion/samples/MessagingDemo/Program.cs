using MassTransit;
using Neo.Infrastructure.Features.Messaging;

namespace Neo.Samples.Messaging;

public static class MessagingProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5087");
        var role = builder.Configuration["Role"] ?? "all";
        if (role is not ("all" or "publisher" or "worker"))
            throw new ArgumentException("Role must be all, publisher or worker.");
        builder.Services.AddSingleton<DemoObservations>();
        builder.Services.AddSingleton<SagaObservations>();
        builder.Services.AddSingleton<DemoOrderServices>();
        builder.Services.AddHealthChecks();
        builder.Services.AddNeoRabbitMq(builder.Configuration, bus =>
        {
            if (role is "all" or "worker")
            {
                bus.AddConsumer<FulfillmentConsumer, FulfillmentDefinition>();
                bus.AddConsumer<AuditConsumer, AuditDefinition>();
                bus.AddDemoOrderSaga();
            }
        });
        var app = builder.Build();
        app.MapHealthChecks("/health/ready");
        if (role is "all" or "publisher")
        {
            app.MapPost("/orders", async (OrderSubmitted message, IPublishEndpoint publisher, CancellationToken ct) =>
            {
                if (message.EventId == Guid.Empty || message.OrderId == Guid.Empty)
                    return Results.BadRequest("EventId and OrderId are required.");
                await publisher.Publish(message, ct);
                // Broker acceptance is not proof of consumer completion.
                return Results.Accepted(value: new { message.EventId, message.OrderId });
            });
            app.MapPost("/sagas/orders", async (StartOrder message, IPublishEndpoint publisher, CancellationToken ct) =>
            {
                if (message.OrderId == Guid.Empty || message.ReleaseFailures is < 0 or > 10)
                    return Results.BadRequest("OrderId required; ReleaseFailures must be 0-10.");
                await publisher.Publish(message, ct);
                return Results.Accepted(value: new { message.OrderId });
            });
            // Local teaching endpoint only; real operator commands require authorization and audit.
            app.MapPost("/sagas/orders/{id:guid}/retry-compensation", async (Guid id, IPublishEndpoint publisher, CancellationToken ct) =>
            {
                await publisher.Publish(new RetryOrderCompensation(id), ct);
                return Results.Accepted();
            });
        }
        if (role is "all" or "worker")
        {
            app.MapGet("/observations", (DemoObservations observations) => observations.Snapshot());
            app.MapGet("/sagas", (SagaObservations observations) => observations.Snapshot());
            app.MapGet("/sagas/effects", (DemoOrderServices services) => services.Snapshot());
        }
        app.Run();
    }
}
