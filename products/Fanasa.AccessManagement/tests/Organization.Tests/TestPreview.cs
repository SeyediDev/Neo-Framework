using System.Security.Claims;
using System.Text.Encodings.Web;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Local test fixture only. Production host never references this assembly.
static class TestPreview
{
    public static async Task Run()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Fanasa.AccessManagement.Web"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot, ApplicationName = typeof(AccessManagementHost).Assembly.GetName().Name, EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:5198");
        var access = new InMemoryAccessManagement(); var tenant = access.GetTenants().Single().Id;
        access.AddUser(new AddTenantUserRequest(tenant, "demo-member", "عضو نمونه"));
        access.RegisterProduct(new RegisterProductRequest("organization-fabric", "ساختار سازمانی", "org", null, "fanasa.ayin", null));
        var data = Path.Combine(Path.GetTempPath(), "fanasa-preview-" + Guid.NewGuid());
        var store = new OrganizationStore(data, access);
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
        builder.Services.AddSingleton(new AccountingStore(data, access));
        builder.Services.AddRazorPages(); builder.Services.AddControllers();
        builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, PreviewIdentity>("test", _ => { }); builder.Services.AddAuthorization();
        var app = builder.Build(); app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        app.MapControllers().RequireAuthorization(); app.MapRazorPages().RequireAuthorization();
        try { await app.RunAsync(); } finally { Directory.Delete(data, true); }
    }
}
sealed class PreviewIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "demo"), new Claim(ClaimTypes.Name, "محیط بررسی رابط"), new Claim("tenant_id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim("permission", "organization.read"), new Claim("permission", "organization.write"), new Claim("permission", "billing.read"), new Claim("permission", "billing.write") }, "test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
    }
}
