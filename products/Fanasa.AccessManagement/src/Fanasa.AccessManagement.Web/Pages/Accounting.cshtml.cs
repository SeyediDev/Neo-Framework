using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace Fanasa.AccessManagement.Web.Pages;
public sealed class AccountingModel(IAccessManagement access) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public bool CanWrite { get; private set; }
    public void OnGet(Guid? tenant)
    {
        Tenants = access.GetTenants().Where(x => x.IsActive && User.HasClaim("tenant_id", x.Id.ToString()) && User.HasClaim("permission", "billing.read")).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
        CanWrite = User.HasClaim("permission", "billing.write");
    }
}
