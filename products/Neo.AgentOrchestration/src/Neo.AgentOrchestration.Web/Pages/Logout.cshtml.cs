using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Workspace");
    public IActionResult OnPost() => SignOut(new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" }, CookieAuthenticationDefaults.AuthenticationScheme);
}
