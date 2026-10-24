using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class UsageAccountingModel(IAccessManagement access) : PageModel
{
    public Tenant[] Tenants { get; private set; } = [];
    public Guid? SelectedTenant { get; private set; }
    public string SelectedTenantName => Tenants.FirstOrDefault(x => x.Id == SelectedTenant)?.DisplayName ?? "";
    public bool IsPreview => User.HasClaim("fanasa.preview", "synthetic");

    public void OnGet(Guid? tenant)
    {
        Tenants = access.GetTenants().Where(x => x.IsActive).OrderBy(x => x.DisplayName, StringComparer.CurrentCulture).ToArray();
        SelectedTenant = Tenants.FirstOrDefault(x => x.Id == tenant)?.Id ?? Tenants.FirstOrDefault()?.Id;
    }
}
