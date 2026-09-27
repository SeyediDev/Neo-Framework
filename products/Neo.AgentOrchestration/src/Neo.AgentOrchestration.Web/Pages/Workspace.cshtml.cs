using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class WorkspaceModel(IConfiguration configuration) : PageModel
{
    public string? Organization => configuration["OrchestrationApi:DefaultOrganizationId"];
    public string? Workspace => configuration["OrchestrationApi:DefaultWorkspaceId"];
    public IActionResult OnGet(Guid? organizationId, Guid? workspaceId)
        => organizationId is { } org && org != Guid.Empty && workspaceId is { } work && work != Guid.Empty
            ? RedirectToPage("/Board", new { organizationId = org, workspaceId = work }) : Page();
}
