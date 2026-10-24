using System.Security.Claims;
using System.Text.Encodings.Web;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.Extensions.Configuration;
using Fanasa.AccessManagement.Web.Governance;

// Local test fixture only. Production host never references this assembly.
static class TestPreview
{
    public static async Task Run()
    {
        var contentRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("FANASA_PREVIEW_CONTENT_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../src/Fanasa.AccessManagement.Web"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot, ApplicationName = typeof(AccessManagementHost).Assembly.GetName().Name, EnvironmentName = "Development" });
        var previewUrl = Environment.GetEnvironmentVariable("FANASA_PREVIEW_URL") ?? "http://127.0.0.1:5198";
        if (!Uri.TryCreate(previewUrl, UriKind.Absolute, out var previewUri) || !previewUri.IsLoopback || previewUri.Scheme != "http")
            throw new InvalidOperationException("Synthetic preview authentication is restricted to loopback HTTP.");
        builder.WebHost.UseUrls(previewUrl);
        var data = Path.Combine(Path.GetTempPath(), "fanasa-preview-" + Guid.NewGuid());
        builder.Configuration["Platform:RegistryPath"] = Path.Combine(data, "catalog.db");
        var registry = new PlatformRegistry(builder.Configuration);
        using var database = new FabricDatabase(Path.Combine(data, "fabric.db"));
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(registry)); var tenant = access.GetTenants().Single().Id;
        registry.SetTenantSource(access.GetTenants);
        registry.SetMembershipSource(access.GetUsers);
        access.AddUser(new AddTenantUserRequest(tenant, "demo-member", "عضو نمونه"));
        access.AddUser(new AddTenantUserRequest(tenant, "demo", "مدیر نمونه"));
        foreach (var permission in TenantGrant.Permissions) access.SetGrant(new(tenant, "demo", permission, null), "test", "داده نمونه");
        access.RegisterProduct(new RegisterProductRequest("organization-fabric", "ساختار سازمانی", "org", null, "fanasa.ayin", null));
        var store = new OrganizationStore(data, access, database: database);
        OrgCommand C(string name, string code, Guid? parent = null) => new(store.Read(tenant).Revision, "unit.save", null, name, code, parent, "department", null, 1, null, null, null, null, "داده نمونه برای بررسی رابط");
        var top = store.Execute(tenant, C("راهبری سازمان", "ORG"), "demo").Units.Single().Id;
        store.Execute(tenant, C("سرمایه انسانی", "HR", top), "demo");
        store.Execute(tenant, C("فناوری و محصول", "TECH", top), "demo");
        store.Execute(tenant, C("مالی و عملیات", "FIN", top), "demo");
        var hr = store.Read(tenant).Units.Single(x => x.Code == "HR").Id;
        var command = C("مدیر منابع انسانی", "HR-MANAGER") with { Operation = "role.save" };
        var role = store.Execute(tenant, command, "demo").Roles!.Single().Id;
        command = C("مدیریت سرمایه انسانی", "HR-LEAD") with { Operation = "position.save", UnitId = hr, RoleId = role };
        var position = store.Execute(tenant, command, "demo").Positions.Single().Id;
        store.Execute(tenant, C("", "") with { Operation = "appointment.add", PositionId = position, Subject = "demo-member", From = DateTimeOffset.UtcNow.AddDays(-1) }, "demo");
        builder.Services.AddSingleton<IAccessManagement>(access); builder.Services.AddSingleton(store);
        var accounting = new AccountingStore(data, access, database: database);
        builder.Services.AddSingleton(accounting);
        UsageAccountingPreview.Register(builder.Services, database, accounting, tenant);
        builder.Services.AddSingleton(access); builder.Services.AddSingleton(registry); builder.Services.AddTransient<IClaimsTransformation, FabricClaimsTransformation>();
        builder.Services.AddSingleton(new Fanasa.AccessManagement.Web.Payments.ZarinpalOptions { Enabled = true, Sandbox = true, MerchantId = Guid.NewGuid().ToString(), CallbackUrl = "https://preview.example/api/payments/zarinpal/callback" });
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<OrganizationCalendarStore>();
        builder.Services.AddSingleton<TemplateOrganizationStore>();
        builder.Services.AddSingleton<PipStore>();
        builder.Services.AddSingleton<IPolicyInformationPoint>(sp => sp.GetRequiredService<PipStore>());
        builder.Services.AddSingleton(new PipTransportOptions()); // No endpoint is approved; this fixture cannot fetch external data.
        builder.Services.AddSingleton<IPipSecretResolver, ConfigurationPipSecretResolver>();
        builder.Services.AddHttpClient<IPipExternalConnector, HttpPipExternalConnector>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => PipHttpTransport.CreateHandler());
        builder.Services.AddSingleton<PipSyncService>();
        builder.Services.AddSingleton<PipDiagnostics>();
        builder.Services.AddSingleton<PipEducationalJourney>();
        builder.Services.AddSingleton<PipEducationalExample>();
        builder.Services.AddSingleton<Fanasa.AccessManagement.Web.Governance.AccessPolicyStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Fanasa.AccessManagement.Web.Governance.IdentityLifecycle>();
        builder.Services.AddSingleton<Fanasa.AccessManagement.Web.Payments.IZarinpalGateway, PreviewPaymentGateway>();
        builder.Services.AddTransient<Fanasa.AccessManagement.Web.Payments.ZarinpalPayments>();
        builder.Services.AddRazorPages(); builder.Services.AddControllers(options => options.Filters.Add<Fanasa.AccessManagement.Web.Api.AccessBoundaryFilter>());
        builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, PreviewIdentity>("test", _ => { })
            .AddScheme<AuthenticationSchemeOptions, PreviewIdentity>(CookieAuthenticationDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
        var app = builder.Build(); app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        if (Environment.GetEnvironmentVariable("FANASA_PREVIEW_PIP_EXAMPLES") == "1")
        {
            var educational = app.Services.GetRequiredService<TemplateOrganizationStore>().Ensure("demo", "دادهٔ مصنوعی بررسی رابط PIP؛ فقط پایگاه دادهٔ موقت");
            app.Services.GetRequiredService<PipEducationalExample>().Ensure(educational.Tenant.Id, "demo", "بررسی رابط نمونهٔ PIP بدون انتشار سیاست");
            app.Services.GetRequiredService<PipEducationalJourney>().Ensure(educational.Tenant.Id, "demo", "بررسی مسیرهای آموزشی با دادهٔ مصنوعی");
            Console.WriteLine("PIP_PREVIEW_ORGANIZATION=" + previewUrl + "/Organization?tenant=" + educational.Tenant.Id + "#organization-pip");
            Console.WriteLine("PIP_PREVIEW_GOVERNANCE=" + previewUrl + "/Governance?tenant=" + educational.Tenant.Id + "#decision-workbench");
        }
        app.MapControllers().RequireAuthorization(); app.MapRazorPages().RequireAuthorization();
        try { await app.RunAsync(); } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(data, true); }
    }
}
// No external payment network calls in this disposable preview.
sealed class PreviewPaymentGateway : Fanasa.AccessManagement.Web.Payments.IZarinpalGateway
{
    public Task<Fanasa.AccessManagement.Web.Payments.GatewayRequest> Request(Fanasa.AccessManagement.Web.Payments.PaymentIntent intent, CancellationToken cancellationToken)
        => Task.FromResult(new Fanasa.AccessManagement.Web.Payments.GatewayRequest("S" + intent.Id.ToString("N") + "000"));
    public Task<Fanasa.AccessManagement.Web.Payments.GatewayVerification> Verify(Fanasa.AccessManagement.Web.Payments.PaymentIntent intent, CancellationToken cancellationToken)
        => Task.FromResult(new Fanasa.AccessManagement.Web.Payments.GatewayVerification(100, 100001));
    public Task<Fanasa.AccessManagement.Web.Payments.GatewayInquiry> Inquiry(Fanasa.AccessManagement.Web.Payments.PaymentIntent intent, CancellationToken cancellationToken)
        => Task.FromResult(new Fanasa.AccessManagement.Web.Payments.GatewayInquiry("PAID"));
}
sealed class PreviewIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "demo"), new Claim(ClaimTypes.Name, "محیط بررسی رابط"),
            new Claim("permission", "platform.admin"), new Claim("fanasa.preview", "synthetic") }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
