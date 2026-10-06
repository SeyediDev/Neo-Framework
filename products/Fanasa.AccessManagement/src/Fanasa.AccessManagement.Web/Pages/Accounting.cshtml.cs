using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Fanasa.AccessManagement.Web.Security;
namespace Fanasa.AccessManagement.Web.Pages;
public sealed class AccountingModel(IAccessManagement access, Fanasa.AccessManagement.Web.Payments.ZarinpalOptions payments) : PageModel
{
    public bool GatewayEnabled => payments.Enabled;
    public bool GatewaySandbox => payments.Sandbox;
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public bool CanWrite { get; private set; }
    public void OnGet(Guid? tenant)
    {
        Tenants = access.GetTenants().Where(x => x.IsActive && TenantAuthorization.Allows(User, x.Id, "billing.read")).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
        CanWrite = SelectedTenant.HasValue && TenantAuthorization.Allows(User, SelectedTenant.Value, "billing.write");
    }
}
