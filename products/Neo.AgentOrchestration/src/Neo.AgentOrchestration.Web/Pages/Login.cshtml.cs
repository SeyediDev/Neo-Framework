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
    public string ReturnUrl { get; private set; } = "/Workspace";
    public IActionResult OnGet(string? returnUrl, bool failed = false)
    {
        Failed = failed;
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Workspace";
        if (failed || !WebIdentity.Configured(configuration)) { Response.StatusCode = failed ? 401 : 503; return Page(); }
        return Challenge(new AuthenticationProperties { RedirectUri = ReturnUrl }, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
