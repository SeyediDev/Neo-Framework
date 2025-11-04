using Neo.Domain.Features.Multilingual;
using Neo.Domain.Features.Multilingual.Implementation;
using Neo.Domain.Features.Telementry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Domain;

public static class DependencyInjectionExtension
{
    public static IServiceCollection AddCandoDomainServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IMultiLingualService, MultiLingualService>();

        services.AddSingleton<ITelementryObject, TelementryObject>();
        services.AddScoped<ITelementryBehaviour, TelementryBehaviour>();
        services.AddScoped<TelemetryInterceptor>();
        return services;
    }
}
