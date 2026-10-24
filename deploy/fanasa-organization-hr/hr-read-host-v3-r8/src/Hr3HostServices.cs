using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fanasa.HrReadHost;

public sealed class Hr3HostPdpOptions
{
    public bool Enabled { get; init; }
    public HrReadPdpCallerSettings? Gateway { get; init; }
    public HrReadReceiverSettings? Receiver { get; init; }
    public Hr3IntrospectionSettings? Introspection { get; init; }
}

/// <summary>Runtime secret reference only. Never log/serialize the configuration or returned credential.</summary>
public sealed class ConfigurationHr3IntrospectionCredentials(IConfiguration configuration) : IHr3IntrospectionCredentials
{
    public ValueTask<Hr3IntrospectionCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientId = configuration["Fanasa:Hr3:Pdp:IntrospectionClientId"];
        var secret = configuration["Fanasa:Hr3:Pdp:IntrospectionClientSecret"];
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)
            ? null : new Hr3IntrospectionCredential(clientId, secret));
    }
}

/// <summary>
/// Explicit runtime opt-in in addition to the build opt-in. No product, client, grant,
/// role, membership, policy, allocation, listener, key or business contract is created.
/// </summary>
public static class Hr3HostServices
{
    public const string ConfigurationSection = "Fanasa:Hr3:Pdp";

    public static IServiceCollection AddHr3HostPdp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>(ConfigurationSection + ":Enabled")) return services;
        var options = configuration.GetSection(ConfigurationSection).Get<Hr3HostPdpOptions>();
        if (options is not { Enabled: true, Gateway: { } gateway, Receiver: { } receiver, Introspection: { } introspection }
            || !HrReadPdpAuthentication.Compatible(gateway, receiver)
            || !introspection.Enabled || introspection.Validate().Count != 0 || introspection.Issuer != gateway.Issuer
            || introspection.GatewayAudience != gateway.Audience || introspection.GatewayRequiredScope != gateway.RequiredScope
            || introspection.MaximumWitnessSeconds is < 1 or > 5)
            throw new InvalidOperationException("Fanasa:Hr3:Pdp requires complete compatible Gateway, Receiver and Introspection settings.");
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHrReadPdpAuthentication(gateway, receiver); // Does not change the default human/OrganizationHrBearer scheme.
        services.AddSingleton<IHr3IntrospectionAuthority>(sp => new Hr3CentralIntrospectionAuthority(
            sp.GetRequiredService<FabricDatabase>(), sp.GetRequiredService<PlatformRegistry>(),
            sp.GetRequiredService<PersistentAccessManagement>(), receiver, gateway, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IHr3IntrospectionCredentials>(new ConfigurationHr3IntrospectionCredentials(configuration));
        services.AddSingleton<IHr3AuthenticationPreparer>(sp => new Hr3KeycloakIntrospectionPreparer(introspection,
            sp.GetRequiredService<IHr3IntrospectionCredentials>(), sp.GetRequiredService<IHr3IntrospectionAuthority>(),
            time: sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHr3HostPdpServiceFactory, ExistingHostHr3PdpServiceFactory>();
        return services;
    }

    public static IEndpointRouteBuilder MapHr3HostPdp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<Hr3HostPdpOptions>();
        if (options is { Enabled: true, Gateway: { } gateway, Receiver: { } receiver })
            Hr3HostPdpEndpoint.MapHr3HostPdp(endpoints, gateway, receiver);
        return endpoints;
    }
}
