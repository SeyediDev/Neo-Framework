using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class NewItemModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet=true)] public Guid ProjectId { get; set; }
    [BindProperty(SupportsGet=true)] public Guid? ParentWorkItemId { get; set; }
    [BindProperty] public string Key { get; set; } = "";
    [BindProperty] public string Title { get; set; } = "";
    [BindProperty] public string Domain { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string ItemType { get; set; } = "Task";
    [BindProperty] public string? AcceptanceCriteria { get; set; }
    [BindProperty] public string Priority { get; set; } = "Normal";
    [BindProperty] public long? EstimatedSeconds { get; set; }
    [BindProperty] public Guid RequestId { get; set; }
    public Task OnGetAsync(CancellationToken ct)
    {
        RequestId = Guid.NewGuid();
        return Attempt(()=>LoadCatalog(ct));
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        WorkItemDetails? created = null;
        if (await Attempt(async()=>created = await Send<WorkItemDetails>("items", new CreateWorkItemRequest(ProjectId,Key,Title,Domain,Description,Priority,ParentWorkItemId,EstimatedSeconds,ItemType,AcceptanceCriteria,RequestId),ct)))
            return RedirectToPage("/Item",new{OrganizationId,WorkspaceId,id=created!.Item.Id});
        await Attempt(()=>LoadCatalog(ct)); return Page();
    }
}
