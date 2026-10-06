using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

public sealed class OrganizationModel(IAccessManagement access) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public bool CanWrite { get; private set; }
    public void OnGet(Guid? tenant)
    {
        Tenants = access.GetTenants().Where(x => x.IsActive && OrgAuthorization.Allows(User, x.Id, false)).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
        CanWrite = SelectedTenant.HasValue && OrgAuthorization.Allows(User, SelectedTenant.Value, true);
    }
}
