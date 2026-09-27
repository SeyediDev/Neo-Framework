using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class RunModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet=true)] public Guid Id { get; set; }
    [BindProperty] public Guid WorkItemVersion { get; set; }
    [BindProperty] public Guid WorkflowVersion { get; set; }
    [BindProperty] public Guid RequestId { get; set; }=Guid.NewGuid();
    public AgentRunDetails? Run { get; private set; }
    public WorkItemDetails? Work { get; private set; }
    public WorkflowView? Flow { get; private set; }
    private async Task Load(CancellationToken ct)
    {
        await LoadCatalog(ct); Run=await Get<AgentRunDetails>($"runs/{Id}",ct);
        Work=await Get<WorkItemDetails>($"items/{Run.Run.WorkItemId}",ct); Flow=Catalog!.Workflows.SingleOrDefault(x=>x.Id==Run.Run.WorkflowId);
    }
    public Task OnGetAsync(CancellationToken ct)=>Attempt(()=>Load(ct));
    public async Task<IActionResult> OnPostEvaluateAsync(CancellationToken ct)
    {
        if(await Attempt(async()=>await Send<AgentRunDetails>($"runs/{Id}/evaluate",new EvaluateAgentRunRequest(RequestId,WorkItemVersion,WorkflowVersion),ct))) return RedirectToPage(new{OrganizationId,WorkspaceId,Id});
        await Attempt(()=>Load(ct));return Page();
    }
    public async Task<IActionResult> OnPostReturnAsync(CancellationToken ct)
    {
        AgentRunDetails? updated=null;
        if(await Attempt(async()=>updated=await Send<AgentRunDetails>($"runs/{Id}/return-assignment",new ReturnRunAssignmentRequest(WorkItemVersion),ct)))
            return RedirectToPage("/Item",new{OrganizationId,WorkspaceId,id=updated!.Run.WorkItemId});
        await Attempt(()=>Load(ct));return Page();
    }
}
