using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace Neo.AgentOrchestration.Web.Pages;

// Public marketing content never loads a workspace or calls the operational API.
[AllowAnonymous]
public sealed class ProductModel : PageModel
{
    public void OnGet() { }
}
