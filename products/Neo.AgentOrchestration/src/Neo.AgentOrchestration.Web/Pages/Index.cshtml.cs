using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class IndexModel(IConfiguration configuration) : PageModel
{
    public ProductInfo? Info { get; private set; }
    public IActionResult OnGet()
    {
        if (Guid.TryParse(configuration["OrchestrationApi:DefaultOrganizationId"], out var organizationId)
            && organizationId != Guid.Empty
            && Guid.TryParse(configuration["OrchestrationApi:DefaultWorkspaceId"], out var workspaceId)
            && workspaceId != Guid.Empty)
            return RedirectToPage("/Board", new { organizationId, workspaceId });
        return RedirectToPage("/Workspace");
    }
}
