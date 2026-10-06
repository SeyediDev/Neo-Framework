using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class IndexModel : PageModel
{
    public ProductInfo? Info { get; private set; }
    public IActionResult OnGet()
    {
        return RedirectToPage("/Workspace");
    }
}
