using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Neo.AgentOrchestration.Web.Pages;
[AllowAnonymous]
public sealed class LoginModel(IConfiguration configuration) : PageModel
{
    public bool Failed { get; private set; }
    public IActionResult OnGet(string? returnUrl, bool failed = false)
    {
        Failed = failed;
        if (failed || !WebIdentity.Configured(configuration)) { Response.StatusCode = failed ? 401 : 503; return Page(); }
        return Challenge(new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl : "/Workspace" }, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
