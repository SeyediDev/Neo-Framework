using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fanasa.AccessManagement.Web;

public static class FabricServices
{
    public static IServiceCollection AddIdentityOrganizationFabric(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration.GetValue("Organization:MeterChanges", false)
            && configuration.GetValue("UsageMetering:Organization:Enabled", false))
            throw new InvalidOperationException("Legacy organization metering and measured organization usage cannot both be enabled.");
        var directory = configuration["Fabric:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "fabric");
        services.AddSingleton(_ => new FabricDatabase(Path.Combine(directory, "fabric.db")));
        services.AddSingleton<PersistentAccessManagement>(sp => new(sp.GetRequiredService<FabricDatabase>(), new InMemoryAccessManagement(sp.GetRequiredService<Platform.PlatformRegistry>())));
        services.Replace(ServiceDescriptor.Singleton<IAccessManagement>(sp => sp.GetRequiredService<PersistentAccessManagement>()));
        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<AccountingStore>(sp => new(directory, sp.GetRequiredService<IAccessManagement>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<FabricDatabase>())));
        services.AddSingleton<OrganizationUsagePriceStore>(sp => new(sp.GetRequiredService<FabricDatabase>(),
            sp.GetRequiredService<IAccessManagement>(), sp.GetRequiredService<Platform.PlatformRegistry>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<OrganizationUsageMetering>(sp => new(configuration, sp.GetRequiredService<IAccessManagement>(),
            sp.GetRequiredService<Platform.PlatformRegistry>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ContractPolicies.ContractPolicyStore>(), sp.GetRequiredService<OrganizationUsagePriceStore>()));
        services.AddSingleton<IUsageSnapshotValidator>(sp => sp.GetRequiredService<OrganizationUsageMetering>());
        services.AddSingleton<MeasuredUsageStore>(sp => new(sp.GetRequiredService<FabricDatabase>(), sp.GetRequiredService<AccountingStore>(),
            sp.GetRequiredService<IUsageSnapshotValidator>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<UsageReconciliationStore>();
        services.AddSingleton<MeasuredUsageRecoveryStore>(sp => new(sp.GetRequiredService<FabricDatabase>(),
            sp.GetRequiredService<MeasuredUsageStore>(), sp.GetRequiredService<TimeProvider>()));
        services.Replace(ServiceDescriptor.Singleton<OrganizationStore>(sp => new(directory, sp.GetRequiredService<IAccessManagement>(),
            configuration.GetValue("Organization:RequireSubscription", !environment.IsDevelopment()), sp.GetRequiredService<FabricDatabase>(),
            configuration.GetValue("Organization:MeterChanges", false) ? sp.GetRequiredService<AccountingStore>() : null,
            sp.GetRequiredService<OrganizationUsageMetering>(), sp.GetRequiredService<MeasuredUsageStore>(),
            sp.GetService<ContractPolicies.IContractPolicyGatewayCommitGuard>(), sp.GetRequiredService<TemporalAccessGate>())));
        services.AddTransient<IClaimsTransformation, FabricClaimsTransformation>();
        services.AddSingleton<OrganizationCalendarStore>();
        services.AddSingleton<TemplateOrganizationStore>();
        services.AddSingleton<Governance.PipStore>();
        services.AddSingleton<Governance.IPolicyInformationPoint>(sp => sp.GetRequiredService<Governance.PipStore>());
        services.AddSingleton<Governance.ConfigurationPipSecretResolver>();
        services.AddSingleton<Governance.IPipHttpClientProvider, Governance.PipHttpClientProvider>();
        var pipOAuth = configuration.GetSection("Pip:OAuth").Get<Governance.PipOAuthOptions>() ?? new();
        pipOAuth.Validate();
        services.AddSingleton(pipOAuth);
        services.AddSingleton<Governance.IPipSecretResolver, Governance.OAuthPipSecretResolver>();
        var pipTransport = configuration.GetSection("Pip:Transport").Get<Governance.PipTransportOptions>() ?? new();
        pipTransport.Validate();
        pipOAuth.Validate(pipTransport);
        services.AddSingleton(pipTransport);
        services.AddHttpClient<Governance.IPipExternalConnector, Governance.HttpPipExternalConnector>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => Governance.PipHttpTransport.CreateHandler());
        services.AddSingleton<Governance.PipSyncService>();
        services.AddSingleton<Governance.PipEducationalExample>();
        services.AddSingleton<Governance.PipDiagnostics>();
        services.AddSingleton<Governance.PipEducationalJourney>();
        var hrAttributes = configuration.GetSection("Organization:HrAttributes").Get<Api.OrganizationHrAttributeOptions>() ?? new();
        hrAttributes.Validate();
        services.AddSingleton(hrAttributes);
        services.AddSingleton<Api.OrganizationHrAttributeProjector>();
        services.AddSingleton(sp => new Api.OrganizationHrAttributeBoundary(hrAttributes,
            sp.GetService<OrganizationHrOptions>() ?? configuration.GetSection("Organization:HrApi").Get<OrganizationHrOptions>() ?? new(),
            sp.GetRequiredService<PersistentAccessManagement>(), sp.GetRequiredService<Platform.PlatformRegistry>(), sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<Governance.PipSyncWorker>();
        // Policy authoring and simulation; runtime temporal restrictions are a separate explicit tenant opt-in.
        services.AddSingleton<Governance.AccessPolicyStore>();
        // Opt-in only: no configured tenants by default and no changes to existing identities or grants.
        var temporalAccess = configuration.GetSection("Organization:TemporalAccess").Get<TemporalAccessOptions>() ?? new();
        temporalAccess.Validate(); services.AddSingleton(temporalAccess);
        services.AddSingleton<TemporalAccessGate>();
        services.AddSingleton<Governance.IdentityLifecycle>();
        ContractPolicies.ContractGatewayContextServices.AddContractPolicyHostWithGatewayContext(services, configuration);
        var usageOutbox = configuration.GetSection("UsageMetering:Outbox").Get<UsageOutboxOptions>() ?? new();
        usageOutbox.Validate(); services.AddSingleton(usageOutbox);
        if (usageOutbox.Enabled)
        {
            services.AddHttpClient<IUsageOutboxTransport, HttpUsageOutboxTransport>(client =>
                client.Timeout = TimeSpan.FromSeconds(usageOutbox.TimeoutSeconds))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton<UsageOutboxDispatcher>(sp => new(sp.GetRequiredService<FabricDatabase>(),
                sp.GetRequiredService<IUsageOutboxTransport>(), usageOutbox.Subscription(), sp.GetRequiredService<TimeProvider>()));
            services.AddHostedService<UsageOutboxWorker>();
        }
        var paymentOptions = configuration.GetSection("Payments:Zarinpal").Get<Payments.ZarinpalOptions>() ?? new();
        if (paymentOptions.Enabled)
        {
            paymentOptions.Validate();
            if (paymentOptions.Sandbox && !environment.IsDevelopment()) throw new InvalidOperationException("درگاه sandbox فقط در محیط Development با داده مستقل مجاز است.");
        }
        services.AddSingleton(paymentOptions);
        services.AddHttpClient<Payments.IZarinpalGateway, Payments.ZarinpalGateway>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<Payments.ZarinpalPayments>();
        services.AddControllers(options => options.Filters.Add<Api.AccessBoundaryFilter>());
        services.AddControllers(options => options.Filters.Add<TemporalAccessFilter>());
        services.AddHostedService<FabricStartup>();
        return services;
    }
}

public sealed class FabricStartup(Platform.PlatformRegistry registry, PersistentAccessManagement access, FabricDatabase database, IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var organizationDirectory = configuration["Organization:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "organization");
        var accountingDirectory = configuration["Accounting:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "accounting");
        var files = (Directory.Exists(organizationDirectory) ? Directory.GetFiles(organizationDirectory, "*.json") : [])
            .Concat(Directory.Exists(accountingDirectory) ? Directory.GetFiles(accountingDirectory, "*.billing.json") : []);
        foreach (var tenant in files.Select(x => Path.GetFileName(x).Replace(".billing.json", "", StringComparison.Ordinal).Replace(".json", "", StringComparison.Ordinal)).Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct())
        {
            var needsImport = (File.Exists(Path.Combine(organizationDirectory, tenant + ".json")) && database.Read<OrgState>("organization", tenant.ToString()) is null)
                || (File.Exists(Path.Combine(accountingDirectory, tenant + ".billing.json")) && database.Read<BillingAccount>("accounting", tenant.ToString()) is null);
            if (!needsImport) continue;
            if (!configuration.GetValue("Fabric:ImportLegacyOnStartup", false)) throw new InvalidOperationException("Legacy organization/accounting data exists. Back it up and explicitly enable Fabric:ImportLegacyOnStartup before cutover.");
            if (!access.GetTenants().Any(x => x.Id == tenant)) throw new InvalidOperationException("Legacy tenant identity must be provisioned from an authoritative source before import.");
            FabricMigration.ImportTenant(database, tenant, organizationDirectory, accountingDirectory);
        }
        registry.SetTenantSource(access.GetTenants);
        registry.SetMembershipSource(access.GetUsers);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
