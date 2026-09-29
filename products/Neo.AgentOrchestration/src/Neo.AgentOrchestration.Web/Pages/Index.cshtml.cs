using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class IndexModel(IConfiguration configuration) : PageModel
{
    public ProductInfo? Info { get; private set; }
    public IActionResult OnGet() => Redirect($"/work/{configuration["OrchestrationApi:DefaultOrganizationId"]}/{configuration["OrchestrationApi:DefaultWorkspaceId"]}");
}
