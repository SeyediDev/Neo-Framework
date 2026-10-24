using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

public sealed class OrganizationModel(IAccessManagement access, TemplateOrganizationStore? examples = null) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public bool CanWrite { get; private set; }
    public bool CanManageCalendars { get; private set; }
    public bool IsTemplateTenant { get; private set; }
    public bool CanReadPip { get; private set; }
    public bool CanManagePip => CanReadPip && !IsTemplateTenant;
    public bool IsAdmin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    public void OnGet(Guid? tenant)
    {
        var admin = User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
        Tenants = access.GetTenants().Where(x => x.IsActive && (OrgAuthorization.Allows(User, x.Id, false) || admin && examples?.IsTemplateTenant(x.Id) == true)).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
        IsTemplateTenant = SelectedTenant.HasValue && examples?.IsTemplateTenant(SelectedTenant.Value) == true;
        CanWrite = SelectedTenant.HasValue && !IsTemplateTenant && OrgAuthorization.Allows(User, SelectedTenant.Value, true);
        CanManageCalendars = SelectedTenant.HasValue && !IsTemplateTenant && (admin || CanWrite && Security.TenantAuthorization.Allows(User, SelectedTenant.Value, "tenancy.manage"));
        CanReadPip = SelectedTenant.HasValue && (admin || Security.TenantAuthorization.Allows(User, SelectedTenant.Value, "tenancy.manage"));
    }
}
