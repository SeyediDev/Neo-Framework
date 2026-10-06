using System.Security.Claims;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Pages;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

static class AuditTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "audit.db"));
        var access = new InMemoryAccessManagement(); var tenant = access.GetTenants().Single().Id;
        var other = access.CreateTenant("audit-other", "Other").Id;
        void Emit(string kind, object payload) => db.Transaction(() => { db.Emit(Guid.NewGuid().ToString(), kind, payload, DateTimeOffset.UtcNow); return true; });
        for (var i = 0; i < 35; i++) Emit("OrganizationChanged", new { TenantId = tenant, Actor = "actor" });
        Emit("OrganizationChanged", new { TenantId = other, Actor = "private" });
        Emit("AccountingChanged", new { TenantId = tenant, Amount = 999, Key = "secret" });
        Emit("TenantMemberAdded", new { Actor = "admin", Result = new { TenantId = tenant } });
        Emit("TenantCreated", new { Result = new { Id = tenant } });
        Emit("PricingPlanCreated", new { Result = new { Id = tenant } });
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("tenant_permission", $"{tenant}:organization.read")], "test"));
        AuditModel Model() => new(access, db) { PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } } };
        var page = Model(); page.OnGet(tenant, null, null);
        check(page.Rows.Length == 30 && page.Next.HasValue && page.Rows.All(x => x.Actor == "actor"), "audit scopes tenant and hides financial events from chart readers");
        var cursor = page.Next; var original = page.Rows.Select(x => x.Id).ToArray();
        Emit("OrganizationChanged", new { TenantId = tenant, Actor = "new" });
        page = Model(); page.OnGet(tenant, null, cursor);
        check(page.Rows.Length == 5 && page.Next is null && !page.Rows.Any(x => original.Contains(x.Id)), "audit keyset pagination stays stable after new insert");
        check(Model().OnGet(other, null, null) is ForbidResult, "audit rejects explicit unauthorized tenant");
        check(Model().OnGet(tenant, "AccountingChanged", null) is BadRequestResult && Model().OnGet(tenant, null, 0) is BadRequestResult, "audit rejects unauthorized filter and invalid cursor");
        check(db.Audit(tenant, ["TenantMemberAdded", "TenantCreated", "PricingPlanCreated"]).Length == 2, "audit recognizes nested tenant events without guessing global ownership");
        user = new ClaimsPrincipal(new ClaimsIdentity([new("tenant_permission", $"{tenant}:billing.read")], "test"));
        page = Model(); page.OnGet(tenant, null, null);
        check(page.Rows.Length == 1 && page.Rows[0].Title == "تغییر حساب" && page.Rows[0].Actor == "ثبت سامانه", "audit financial summary excludes raw payload and unrelated events");
        user = new ClaimsPrincipal(); page = Model(); page.OnGet(null, null, null);
        check(page.Tenants.Length == 0 && page.Rows.Length == 0, "anonymous audit exposes no tenants or events");
    }
}
