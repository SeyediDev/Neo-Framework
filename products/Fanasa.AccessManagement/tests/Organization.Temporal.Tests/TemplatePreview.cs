using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Test assembly only. The production host never references this project or authentication handler.
// Persistence is deliberate: closing or restarting the preview must not erase the educational tenant.
static class TemplatePreview
{
    private const string DataDirectory = "E:/SJVS/Projects/Neo/.build/organization-template-preview";
    private const string Origin = "http://127.0.0.1:5201";

    public static async Task Run()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(DataDirectory, "../../products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web"));
        if (!Directory.Exists(Path.Combine(contentRoot, "wwwroot"))) throw new DirectoryNotFoundException("Actual host static assets were not found.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot"),
            ApplicationName = typeof(AccessManagementHost).Assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        // No deployment settings, SSO secrets, environment listener overrides or external endpoints enter the fixture.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = Path.Combine(DataDirectory, "catalog.db"),
            ["AllowedHosts"] = "127.0.0.1"
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5201));

        var registry = new PlatformRegistry(builder.Configuration);
        using var database = new FabricDatabase(Path.Combine(DataDirectory, "fabric.db"));
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(registry));
        registry.SetTenantSource(access.GetTenants); registry.SetMembershipSource(access.GetUsers);
        var clock = TimeProvider.System;
        var organization = new OrganizationStore(DataDirectory, access, database: database);
        var calendars = new OrganizationCalendarStore(database, access, clock);
        var policies = new AccessPolicyStore(database, access, calendars);
        var examples = new TemplateOrganizationStore(database, access, calendars, policies, clock);
        var template = examples.Ensure("local-template-preview-bootstrap", "ساخت پایدار سازمان الگوی آموزشی در دیتابیس اختصاصی پیش‌نمایش محلی.");

        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton<IAccessManagement>(access);
        builder.Services.AddSingleton(organization);
        builder.Services.AddSingleton(calendars);
        builder.Services.AddSingleton(policies);
        builder.Services.AddSingleton(examples);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<IdentityLifecycle>();
        builder.Services.AddSingleton(new TemporalAccessOptions { Enabled = false, TenantIds = [] });
        builder.Services.AddSingleton<TemporalAccessGate>();
        builder.Services.AddScoped<TemporalAccessFilter>();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(AccessManagementHost).Assembly);
        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<AccessBoundaryFilter>();
            options.Filters.Add<TemporalAccessFilter>();
        }).AddApplicationPart(typeof(AccessManagementHost).Assembly);
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication("template-preview-test")
            .AddScheme<AuthenticationSchemeOptions, TemplatePreviewIdentity>("template-preview-test", _ => { })
            .AddScheme<AuthenticationSchemeOptions, TemplatePreviewIdentity>(CookieAuthenticationDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
                || context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != 5201)
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            context.Response.Headers["X-Fanasa-Preview"] = "synthetic-local-template";
            await next();
        });
        app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        app.MapControllers().RequireAuthorization(); app.MapRazorPages().RequireAuthorization();
        var organizationUrl = Origin + "/Organization?tenant=" + template.Tenant.Id;
        var governanceUrl = Origin + "/Governance?tenant=" + template.Tenant.Id;
        Console.WriteLine("PREVIEW_MODE=synthetic-local-only");
        Console.WriteLine("PREVIEW_DATABASE=" + Path.Combine(DataDirectory, "fabric.db"));
        Console.WriteLine("PREVIEW_TENANT=" + template.Tenant.Id);
        Console.WriteLine("PREVIEW_ORGANIZATION=" + organizationUrl);
        Console.WriteLine("PREVIEW_GOVERNANCE=" + governanceUrl);
        Console.WriteLine("PREVIEW_SCENARIOS=" + Origin + "/api/organization/template/scenarios");
        await app.RunAsync();
    }
}

sealed class TemplatePreviewIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new("sub", "local-template-preview-operator"),
            new(ClaimTypes.Name, "پیش‌نمایش محلی با هویت مصنوعی"),
            new("permission", "platform.admin"),
            new("fanasa.preview", "synthetic")
        ], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
