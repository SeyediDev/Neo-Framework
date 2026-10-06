using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;
public sealed class GovernanceModel(IAccessManagement access) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public string Subject => User.Identity?.IsAuthenticated == true ? OrgAuthorization.Subject(User) : "";
    public bool IsAdmin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    public bool CanReview => SelectedTenant.HasValue && TenantAuthorization.Allows(User, SelectedTenant.Value, "tenancy.manage") && TenantAuthorization.Allows(User, SelectedTenant.Value, "organization.write");
    public IActionResult OnGet(Guid? tenant)
    {
        if (!ModelState.IsValid) return BadRequest();
        Tenants = access.GetTenants().Where(t => t.IsActive && TenantAuthorization.Allows(User, t.Id, "tenancy.read") && TenantAuthorization.Allows(User, t.Id, "organization.read")).ToArray();
        if (tenant.HasValue && !Tenants.Any(x => x.Id == tenant)) return Forbid();
        SelectedTenant = tenant ?? Tenants.FirstOrDefault()?.Id;
        return Page();
    }
}
