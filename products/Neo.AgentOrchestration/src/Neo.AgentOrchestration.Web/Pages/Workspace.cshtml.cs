using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Web;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class WorkspaceModel(OrchestrationClient client) : PageModel
{
    public AccessibleWorkspaceCatalog Access { get; private set; } = new([]);
    public string IdentityName => User.FindFirst("name")?.Value ?? User.FindFirst("preferred_username")?.Value ?? User.FindFirst("email")?.Value ?? "کاربر سازمانی";
    public string? IdentityEmail => User.FindFirst("email")?.Value;
    public string IdentitySubject => User.FindFirst("sub")?.Value ?? "نشست احراز‌شده";
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync(Guid? organizationId, Guid? workspaceId, CancellationToken ct)
    {
        try { Access = await client.GetAccessibleWorkspacesAsync(ct); }
        catch (WebApiException e) { Error = e.Message; return Page(); }
        if (organizationId is { } org && workspaceId is { } work)
        {
            var allowed = Access.Organizations.SelectMany(x => x.Workspaces).Any(x => x.OrganizationId == org && x.Id == work);
            if (allowed) return RedirectToPage("/Board", new { organizationId = org, workspaceId = work });
            Error = "این نشست به فضای کاری انتخاب‌شده دسترسی ندارد.";
        }
        return Page();
    }
}
