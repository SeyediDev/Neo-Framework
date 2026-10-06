using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fanasa.AccessManagement.Web;

public static class FabricServices
{
    public static IServiceCollection AddIdentityOrganizationFabric(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var directory = configuration["Fabric:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "fabric");
        services.AddSingleton(_ => new FabricDatabase(Path.Combine(directory, "fabric.db")));
        services.AddSingleton<PersistentAccessManagement>(sp => new(sp.GetRequiredService<FabricDatabase>(), new InMemoryAccessManagement(sp.GetRequiredService<Platform.PlatformRegistry>())));
        services.Replace(ServiceDescriptor.Singleton<IAccessManagement>(sp => sp.GetRequiredService<PersistentAccessManagement>()));
        services.Replace(ServiceDescriptor.Singleton<AccountingStore>(sp => new(directory, sp.GetRequiredService<IAccessManagement>(), database: sp.GetRequiredService<FabricDatabase>())));
        services.Replace(ServiceDescriptor.Singleton<OrganizationStore>(sp => new(directory, sp.GetRequiredService<IAccessManagement>(),
            configuration.GetValue("Organization:RequireSubscription", !environment.IsDevelopment()), sp.GetRequiredService<FabricDatabase>(),
            configuration.GetValue("Organization:MeterChanges", false) ? sp.GetRequiredService<AccountingStore>() : null)));
        services.AddTransient<IClaimsTransformation, FabricClaimsTransformation>();
        // Governance authoring/simulation only. Live enforcement and scheduled execution are not enabled here.
        services.AddSingleton<Governance.AccessPolicyStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Governance.IdentityLifecycle>();
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
