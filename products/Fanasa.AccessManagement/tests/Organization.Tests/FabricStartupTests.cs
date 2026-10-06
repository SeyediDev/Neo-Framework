using Fanasa.AccessManagement.Web;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

static class FabricStartupTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "startup"); Directory.CreateDirectory(root);
        var environment = new Environment { ContentRootPath = root };
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = Path.Combine(root, "catalog.db") }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<PlatformRegistry>(_ => new(settings));
        services.AddIdentityOrganizationFabric(settings, environment);
        using var provider = services.BuildServiceProvider();
        var database = provider.GetRequiredService<FabricDatabase>(); var access = provider.GetRequiredService<PersistentAccessManagement>();
        check(ReferenceEquals(provider.GetRequiredService<IAccessManagement>(), access) && ReferenceEquals(provider.GetRequiredService<AccountingStore>().Database, database), "host registration shares persistent access and transaction database");
        var legacy = new OrganizationStore(Path.Combine(root, "App_Data", "organization"), access); var tenant = access.GetTenants().Single().Id;
        legacy.Execute(tenant, new(0, "unit.save", null, "Legacy", "legacy", null, "team", null, 1, null, null, null, null, "test"), "test");
        var startup = new FabricStartup(provider.GetRequiredService<PlatformRegistry>(), access, database, settings, environment);
        try { startup.StartAsync(default).GetAwaiter().GetResult(); throw new Exception("Expected legacy startup rejection"); }
        catch (InvalidOperationException) { check(true, "startup refuses silently abandoning existing legacy chart"); }
        settings["Fabric:ImportLegacyOnStartup"] = "true"; startup.StartAsync(default).GetAwaiter().GetResult();
        check(provider.GetRequiredService<OrganizationStore>().Read(tenant).Revision == 1, "explicit startup cutover imports known tenant chart");
        access.AddUser(new(tenant, "developer", null)); var registry = provider.GetRequiredService<PlatformRegistry>();
        registry.Grant(new("developer", tenant, "developer.launch", null), "test");
        check(registry.Allows("developer", tenant, "developer.launch"), "startup connects authoritative membership resolver");
        var production = new Environment { ContentRootPath = root, EnvironmentName = "Production" };
        settings["Payments:Zarinpal:Enabled"] = "true"; settings["Payments:Zarinpal:Sandbox"] = "true";
        settings["Payments:Zarinpal:MerchantId"] = Guid.NewGuid().ToString(); settings["Payments:Zarinpal:CallbackUrl"] = "https://example.com/api/payments/zarinpal/callback";
        try { new ServiceCollection().AddIdentityOrganizationFabric(settings, production); throw new Exception("Expected sandbox production rejection"); }
        catch (InvalidOperationException) { check(true, "production refuses sandbox credits"); }
    }
    sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Fanasa.AccessManagement.Web";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
