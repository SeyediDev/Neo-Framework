using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class ItemModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet=true)] public Guid Id { get; set; }
    [BindProperty] public Guid Version { get; set; }
    [BindProperty] public Guid RoleId { get; set; }
    [BindProperty] public string? Branch { get; set; }
    [BindProperty] public string NextStatus { get; set; } = "Ready";
    [BindProperty] public string? Message { get; set; }
    [BindProperty] public long? Seconds { get; set; }
    [BindProperty] public Guid DependencyId { get; set; }
    [BindProperty] public string Kind { get; set; } = "Commit";
    [BindProperty] public string Reference { get; set; } = "";
    [BindProperty] public string Outcome { get; set; } = "NotApplicable";
    [BindProperty] public string? CommitSha { get; set; }
    [BindProperty] public Guid WorkflowId { get; set; }
    [BindProperty] public Guid WorkflowVersion { get; set; }
    [BindProperty] public Guid TransitionId { get; set; }
    [BindProperty] public bool Approved { get; set; }
    [BindProperty] public Guid RequestId { get; set; } = Guid.NewGuid();
    [BindProperty] public Guid? AgentProfileId { get; set; }
    [BindProperty] public string SimulationOutcome { get; set; } = "Succeeded";
    [BindProperty] public bool AllowExternalExecution { get; set; }
    public WorkItemDetails? Details { get; private set; }
    public IReadOnlyList<AgentRunDetails> Runs { get; private set; } = [];
    public IReadOnlyList<WorkflowApprovalView> Approvals { get; private set; } = [];
    public WorkflowPlan? Plan { get; private set; }
    public Task OnGetAsync(CancellationToken ct) => Attempt(()=>Load(ct));
    private async Task Load(CancellationToken ct)
    {
        await LoadCatalog(ct); Details=await Get<WorkItemDetails>($"items/{Id}",ct);
        Runs=await Get<AgentRunDetails[]>($"items/{Id}/runs",ct);
        var approvals=new List<WorkflowApprovalView>();
        foreach(var flow in Catalog!.Workflows.Where(x=>x.ProjectId==Details.Item.ProjectId))
            approvals.AddRange(await Get<WorkflowApprovalView[]>($"workflows/{flow.Id}/approvals?workItemId={Id}",ct));
        Approvals=approvals.OrderByDescending(x=>x.CreatedAtUtc).ToArray();
    }
    private async Task<IActionResult> Change(string path,object body,CancellationToken ct,bool put=false)
    {
        if(await Attempt(async()=>await Send<WorkItemDetails>($"items/{Id}/{path}",body,ct,put))) return RedirectToPage(new{OrganizationId,WorkspaceId,Id});
        await Attempt(()=>Load(ct));return Page();
    }
    public Task<IActionResult> OnPostClaimAsync(CancellationToken ct)=>Change("claim",new ClaimWorkItemRequest(Version,RoleId,Branch),ct);
    public Task<IActionResult> OnPostStatusAsync(CancellationToken ct)=>Change("status",new ChangeStatusRequest(Version,NextStatus,Message),ct);
    public Task<IActionResult> OnPostLogAsync(CancellationToken ct)=>Change("logs",new AppendLogRequest(Version,Message??""),ct);
    public Task<IActionResult> OnPostEstimateAsync(CancellationToken ct)=>Change("estimate",new SetEstimateRequest(Version,Seconds),ct,true);
    public Task<IActionResult> OnPostStartTimeAsync(CancellationToken ct)=>Change("time/start",new VersionRequest(Version),ct);
    public Task<IActionResult> OnPostStopTimeAsync(CancellationToken ct)=>Change("time/stop",new VersionRequest(Version),ct);
    public Task<IActionResult> OnPostArchiveAsync(CancellationToken ct)=>Change("archive",new VersionRequest(Version),ct);
    public Task<IActionResult> OnPostRestoreAsync(CancellationToken ct)=>Change("restore",new VersionRequest(Version),ct);
    public Task<IActionResult> OnPostDependencyAsync(CancellationToken ct)=>Change("dependencies",new AddDependencyRequest(Version,DependencyId),ct);
    public Task<IActionResult> OnPostEvidenceAsync(CancellationToken ct)=>Change("evidence",new AddEvidenceRequest(Version,Kind,Reference,Outcome,Message,CommitSha),ct);
    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        AgentRunDetails? run=null;
        if(await Attempt(async()=>run=await Send<AgentRunDetails>($"items/{Id}/runs",new StartAgentRunRequest(RequestId,Version,WorkflowId,WorkflowVersion,RoleId,AgentProfileId,Branch,SimulationOutcome,AllowExternalExecution),ct)))
            return RedirectToPage("/Run",new{OrganizationId,WorkspaceId,id=run!.Run.Id});
        await Attempt(()=>Load(ct));return Page();
    }
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    { await Attempt(async()=>Plan=await Send<WorkflowPlan>($"workflows/{WorkflowId}/preview",new PreviewWorkflowRequest(Id,TransitionId),ct));await Attempt(()=>Load(ct));return Page(); }
    public async Task<IActionResult> OnPostApproveAsync(CancellationToken ct)
    {
        if(await Attempt(async()=>await Send<WorkflowApprovalView>($"workflows/{WorkflowId}/approvals",new ApproveWorkflowRequest(WorkflowVersion,Id,Version,TransitionId,Approved,Message??""),ct)))
            return RedirectToPage(new{OrganizationId,WorkspaceId,Id});
        await Attempt(()=>Load(ct));return Page();
    }
}
