using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Neo.Domain.Features.Telementry;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScopedWithTelemetry<TService, TImpl>(this IServiceCollection services)
        where TService : class
        where TImpl : class, TService
    {
        if (!typeof(TService).IsInterface)
            throw new ArgumentException("Telemetry registration requires an interface service type.", nameof(TService));
        // Keep the concrete service container-owned for dependency injection and disposal.
        services.TryAddScoped<TImpl>();
        services.AddScoped<TService>(provider => TelemetryProxyFactory.Create<TService>(
            provider.GetRequiredService<TImpl>(), provider.GetRequiredService<ITelementryBehaviour>()));
        return services;
    }
}
