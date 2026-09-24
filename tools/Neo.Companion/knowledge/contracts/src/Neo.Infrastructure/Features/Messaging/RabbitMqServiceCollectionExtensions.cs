using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Infrastructure.Features.Messaging;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Registers one MassTransit bus explicitly. Register consumers/definitions in configureConsumers.
    /// Configure retry/outbox per consumer definition; no scheduler or retry-every-exception policy is installed.
    /// </summary>
    public static IServiceCollection AddNeoRabbitMq(this IServiceCollection services, IConfiguration configuration,
        Action<IBusRegistrationConfigurator> configureConsumers,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureTransport = null,
        string sectionName = "RabbitMq")
    {
        ArgumentNullException.ThrowIfNull(configureConsumers);
        var options = configuration.GetSection(sectionName).Get<NeoRabbitMqOptions>()
            ?? throw new ArgumentException($"Missing configuration section: {sectionName}");
        options.Validate();
        services.Configure<MassTransitHostOptions>(host =>
        {
            host.WaitUntilStarted = true;
            host.StartTimeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds);
            host.StopTimeout = TimeSpan.FromSeconds(30);
        });
        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(options.EndpointPrefix, false));
            configureConsumers(bus);
            bus.UsingRabbitMq((context, transport) =>
            {
                transport.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });
                transport.PrefetchCount = options.PrefetchCount;
                transport.ConcurrentMessageLimit = options.ConcurrentMessageLimit;
                configureTransport?.Invoke(context, transport);
                // Last: apply definitions to all registered consumers, one queue per consumer by default.
                transport.ConfigureEndpoints(context);
            });
        });
        return services;
    }
}
