using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;
public sealed class TenancyModel(IAccessManagement access, TemplateOrganizationStore? examples = null) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public bool CanManage { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool IsTemplateTenant { get; private set; }
    public void OnGet(Guid? tenant)
    {
        IsAdmin = User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
        Tenants = access.GetTenants().Where(x => x.IsActive && (IsAdmin || TenantAuthorization.Allows(User, x.Id, "tenancy.read"))).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
        IsTemplateTenant = SelectedTenant.HasValue && examples?.IsTemplateTenant(SelectedTenant.Value) == true;
        CanManage = SelectedTenant.HasValue && !IsTemplateTenant && (IsAdmin || TenantAuthorization.Allows(User, SelectedTenant.Value, "tenancy.manage"));
    }
}
