using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;
public sealed record AuditRow(long Id, string Title, string Actor, DateTimeOffset RecordedAt);
public sealed class AuditModel(IAccessManagement access, FabricDatabase database) : PageModel
{
    private static readonly (string Kind, string Permission, string Title)[] Catalog = [
        ("OrganizationChanged", "organization.read", "تغییر ساختار سازمانی"),
        ("AccountingChanged", "billing.read", "تغییر حساب"),
        ("PaymentApplied", "billing.read", "اعمال پرداخت تأییدشده"),
        ("PaymentReviewed", "billing.read", "بررسی وضعیت پرداخت"),
        ("TenantCreated", "tenancy.read", "ایجاد سازمان"),
        ("TenantMemberAdded", "tenancy.read", "افزودن عضو"),
        ("TenantSubscribed", "tenancy.read", "ثبت اشتراک"),
        ("TenantGrantChanged", "tenancy.read", "تغییر مجوز سازمانی"),
        ("TenantMembershipChanged", "tenancy.read", "تغییر وضعیت عضویت"),
        ("TenantSubscriptionChanged", "tenancy.read", "تغییر وضعیت اشتراک"),
        ("PolicyDrafted", "tenancy.read", "تعریف پیش‌نویس سیاست"),
        ("PolicyPublished", "tenancy.read", "انتشار سیاست"),
        ("PolicyRetired", "tenancy.read", "بازنشستگی سیاست"),
        ("AccessDecisionApplied", "tenancy.read", "ثبت تصمیم دسترسی"),
        ("LifecycleRequested", "tenancy.read", "ثبت درخواست چرخه هویت"),
        ("LifecycleReviewed", "tenancy.read", "بررسی درخواست چرخه هویت"),
        ("LifecycleCancelled", "tenancy.read", "لغو درخواست چرخه هویت"),
        ("LifecycleApplied", "tenancy.read", "اعمال تغییر چرخه هویت"),
        ("LifecycleFailed", "tenancy.read", "توقف اجرای درخواست هویت")];
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public string? SelectedKind { get; private set; }
    public (string Kind, string Title)[] Filters { get; private set; } = [];
    public AuditRow[] Rows { get; private set; } = [];
    public long? Next { get; private set; }
    public IActionResult OnGet(Guid? tenant, string? kind, long? before)
    {
        if (!ModelState.IsValid || before is <= 0) return BadRequest();
        Tenants = access.GetTenants().Where(x => x.IsActive && Catalog.Any(c => TenantAuthorization.Allows(User, x.Id, c.Permission))).ToArray();
        if (tenant.HasValue && !Tenants.Any(x => x.Id == tenant)) return Forbid();
        SelectedTenant = tenant ?? Tenants.FirstOrDefault()?.Id;
        if (!SelectedTenant.HasValue) return Page();
        var allowed = Catalog.Where(c => TenantAuthorization.Allows(User, SelectedTenant.Value, c.Permission)).ToArray();
        Filters = allowed.Select(c => (c.Kind, c.Title)).ToArray();
        if (!string.IsNullOrEmpty(kind) && !allowed.Any(c => c.Kind == kind)) return BadRequest();
        SelectedKind = kind;
        var events = database.Audit(SelectedTenant.Value, allowed.Where(c => string.IsNullOrEmpty(kind) || c.Kind == kind).Select(c => c.Kind).ToArray(), before ?? long.MaxValue, 31);
        Rows = events.Take(30).Select(e =>
        {
            using var payload = JsonDocument.Parse(e.Payload);
            var actor = payload.RootElement.TryGetProperty("Actor", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "ثبت سامانه";
            return new AuditRow(e.Id, allowed.Single(c => c.Kind == e.Kind).Title, actor, e.RecordedAt);
        }).ToArray();
        Next = events.Length > 30 ? Rows[^1].Id : null;
        return Page();
    }
}
