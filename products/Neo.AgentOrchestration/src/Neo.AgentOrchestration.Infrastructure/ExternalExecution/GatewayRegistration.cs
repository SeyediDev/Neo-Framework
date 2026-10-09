using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.Runs;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public static class GatewayRegistration
{
    // Internal composition only. Neither API/Worker nor migration calls this.
    // It does not start a host, dispatcher, scheduler or native runtime.
    public static IServiceCollection AddGatewayJournal(this IServiceCollection services,
        IConfiguration configuration, string connection)
    {
        var database = configuration["AgentGateway:DatabaseName"] ?? "FanasaAgentGateway";
        GatewayProvisioner.ValidateDestination(connection, database);
        services.AddDbContextFactory<GatewayDbContext>(o => o.UseSqlServer(connection));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IHarnessSecrets, EnvironmentHarnessSecrets>();
        services.AddNativeAgentAdapters();
        services.AddSingleton<ConfiguredGatewayBindings>();
        services.AddSingleton<IGatewayBindings>(sp => sp.GetRequiredService<ConfiguredGatewayBindings>());
        services.AddScoped<IGatewayJournal, SqlGatewayJournal>();
        services.AddScoped<GatewayExecution>();
        services.TryAddScoped<IGatewaySandbox, DisabledGatewaySandbox>();
        services.AddHttpClient<HttpGatewayResultDelivery>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddScoped<IGatewayResultDelivery>(sp => sp.GetRequiredService<HttpGatewayResultDelivery>());
        return services;
    }
}
