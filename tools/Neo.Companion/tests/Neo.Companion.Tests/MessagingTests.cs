using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neo.Infrastructure.Features.Messaging;
using Neo.Samples.Messaging;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class MessagingTests
{
    [Fact]
    public void Missing_section_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddNeoRabbitMq(new ConfigurationBuilder().Build(), _ => { }));
    }

    [Fact]
    public async Task Registration_configures_bounded_startup_and_consumer_names_without_starting_bus()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Username"] = "demo", ["RabbitMq:Password"] = "local",
            ["RabbitMq:EndpointPrefix"] = "test-orders", ["RabbitMq:StartupTimeoutSeconds"] = "12"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DemoObservations>();
        services.AddNeoRabbitMq(config, bus => bus.AddConsumer<AuditConsumer, AuditDefinition>());
        await using var provider = services.BuildServiceProvider(true);
        var options = provider.GetRequiredService<IOptions<MassTransitHostOptions>>().Value;
        Assert.True(options.WaitUntilStarted);
        Assert.Equal(TimeSpan.FromSeconds(12), options.StartTimeout);
        Assert.Equal("test-orders-audit", provider.GetRequiredService<IEndpointNameFormatter>().Consumer<AuditConsumer>());
    }

    [Theory]
    [InlineData("RabbitMq:Username", "")]
    [InlineData("RabbitMq:EndpointPrefix", "Bad Prefix")]
    [InlineData("RabbitMq:ConcurrentMessageLimit", "99")]
    [InlineData("RabbitMq:StartupTimeoutSeconds", "0")]
    public void Invalid_configuration_fails_before_connecting(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RabbitMq:Username"] = "demo", ["RabbitMq:Password"] = "test-password",
            [key] = value
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddNeoRabbitMq(config, _ => { }));
        Assert.DoesNotContain("test-password", error.Message);
    }

    [Fact]
    public async Task Publish_reaches_both_consumers_retries_transient_failure_and_faults_business_failure()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DemoObservations>();
        services.AddSingleton<FaultObservation>();
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<FulfillmentConsumer, FulfillmentDefinition>();
            bus.AddConsumer<AuditConsumer, AuditDefinition>();
            bus.AddConsumer<FaultConsumer>();
            bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });
        await using var provider = services.BuildServiceProvider(true);
        var bus = provider.GetRequiredService<IBusControl>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bus.StartAsync(timeout.Token);
        try
        {
            using var scope = provider.CreateScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            var state = provider.GetRequiredService<DemoObservations>();
            var message = new OrderSubmitted(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, FailOnce: true);
            await publisher.Publish(message, timeout.Token);
            await Until(() => state.Snapshot().Count(x => x.Value.Completed) == 2, timeout.Token);
            Assert.Equal(2, state.Snapshot()[$"fulfillment:{message.EventId}"].Attempts);
            Assert.Equal(1, state.Snapshot()[$"audit:{message.EventId}"].Attempts);

            // Same business event is replayed, then a later event fences each sequential endpoint.
            await publisher.Publish(message, timeout.Token);
            var fence = message with { EventId = Guid.NewGuid(), FailOnce = false };
            await publisher.Publish(fence, timeout.Token);
            await Until(() => state.Snapshot().Count(x => x.Value.Completed) == 4, timeout.Token);
            Assert.Equal(2, state.Snapshot()[$"fulfillment:{message.EventId}"].Attempts);

            var broken = message with { EventId = Guid.NewGuid(), FailOnce = false, FailPermanently = true };
            await publisher.Publish(broken, timeout.Token);
            var fault = await provider.GetRequiredService<FaultObservation>().Received.Task.WaitAsync(timeout.Token);
            Assert.Equal(broken.EventId, fault);
            Assert.Equal(1, state.Snapshot()[$"fulfillment:{broken.EventId}"].Attempts);
            Assert.False(state.Snapshot()[$"fulfillment:{broken.EventId}"].Completed);
        }
        finally { await bus.StopAsync(CancellationToken.None); }
    }

    private static async Task Until(Func<bool> done, CancellationToken token)
    {
        while (!done()) await Task.Delay(20, token);
    }
}

public sealed class FaultObservation
{
    public TaskCompletionSource<Guid> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
public sealed class FaultConsumer(FaultObservation observation) : IConsumer<Fault<OrderSubmitted>>
{
    public Task Consume(ConsumeContext<Fault<OrderSubmitted>> context)
    {
        observation.Received.TrySetResult(context.Message.Message.EventId);
        return Task.CompletedTask;
    }
}
