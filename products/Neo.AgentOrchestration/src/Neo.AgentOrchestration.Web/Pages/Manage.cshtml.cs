using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class ManageModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty] public Guid Id { get; set; }
    [BindProperty] public Guid Version { get; set; }
    [BindProperty] public Guid ProjectId { get; set; }
    [BindProperty] public Guid RoleId { get; set; }
    [BindProperty] public string Key { get; set; }="";
    [BindProperty] public string Name { get; set; }="";
    [BindProperty] public string? ScopeDescription { get; set; }
    [BindProperty] public int MaxConcurrentWorkItems { get; set; } = 1;
    [BindProperty] public string Provider { get; set; }="fake";
    [BindProperty] public string? AgentModel { get; set; }
    [BindProperty] public string? Instructions { get; set; }
    [BindProperty] public string? SkillPath { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public string RepositoryProvider { get; set; } = "";
    [BindProperty] public string RepositoryUrl { get; set; } = "";
    [BindProperty] public string RepositoryKey { get; set; } = "";
    [BindProperty] public string RepositoryDefaultBranch { get; set; } = "main";
    [BindProperty] public string? RepositoryDevelopmentBranch { get; set; }
    [BindProperty] public string? RepositoryCiCdReference { get; set; }
    [BindProperty] public string? RepositorySecretReference { get; set; }
    [BindProperty] public bool RepositoryEnabled { get; set; } = true;
    [BindProperty] public Guid FromRoleId { get; set; }
    [BindProperty] public Guid? ToRoleId { get; set; }
    [BindProperty] public string FromStatus { get; set; }="Review";
    [BindProperty] public string ToStatus { get; set; }="Ready";
    [BindProperty] public bool RequireCommit { get; set; }
    [BindProperty] public bool RequirePassingTests { get; set; }
    [BindProperty] public bool RequireApproval { get; set; }
    [BindProperty] public string? RequiredArtifact { get; set; }
    public Task OnGetAsync(CancellationToken ct)=>Attempt(()=>LoadCatalog(ct));
    private async Task<IActionResult> Change(string resource,object? body,CancellationToken ct,bool put=false)
    {
        if(await Attempt(async()=>await Send<WorkspaceCatalog>(resource,body,ct,put)))return RedirectToPage(new{OrganizationId,WorkspaceId});
        await Attempt(()=>LoadCatalog(ct));return Page();
    }
    public Task<IActionResult> OnPostProjectAsync(CancellationToken ct)=>Id==Guid.Empty
        ?Change("projects",new CreateProjectRequest(Key,Name),ct):Change($"projects/{Id}",new RenameProjectRequest(Name),ct,true);
    public Task<IActionResult> OnPostDisableProjectAsync(CancellationToken ct)=>Change($"projects/{Id}/disable",null,ct);
    public Task<IActionResult> OnPostRepositoryAsync(CancellationToken ct)=>Change($"projects/{Id}/repository", new UpsertProjectRepositoryBindingRequest(RepositoryProvider, RepositoryUrl, RepositoryKey, RepositoryDefaultBranch, RepositoryDevelopmentBranch, RepositoryCiCdReference, RepositorySecretReference, RepositoryEnabled), ct, true);
    public Task<IActionResult> OnPostRoleAsync(CancellationToken ct)=>Id==Guid.Empty
        ?Change("roles",new CreateRoleProfileRequest(Key,Name,ScopeDescription,MaxConcurrentWorkItems),ct):Change($"roles/{Id}",new UpdateRoleProfileRequest(Name,ScopeDescription,MaxConcurrentWorkItems),ct,true);
    public Task<IActionResult> OnPostRoleEnabledAsync(CancellationToken ct)=>Change($"roles/{Id}/enabled",new SetProfileEnabledRequest(Enabled),ct,true);
    public Task<IActionResult> OnPostAgentAsync(CancellationToken ct)=>Id==Guid.Empty
        ?Change("agents",new CreateAgentProfileRequest(RoleId,Key,Name,Provider,AgentModel,Instructions,SkillPath),ct)
        :Change($"agents/{Id}",new UpdateAgentProfileRequest(Name,Provider,AgentModel,Instructions,SkillPath),ct,true);
    public Task<IActionResult> OnPostAgentEnabledAsync(CancellationToken ct)=>Change($"agents/{Id}/enabled",new SetProfileEnabledRequest(Enabled),ct,true);
    public Task<IActionResult> OnPostWorkflowAsync(CancellationToken ct)=>Id==Guid.Empty
        ?Change("workflows",new CreateWorkflowRequest(ProjectId,Key,Name),ct)
        :Change($"workflows/{Id}",new UpdateWorkflowRequest(Version,Name,Enabled),ct,true);
    public Task<IActionResult> OnPostTransitionAsync(CancellationToken ct)=>Change($"workflows/{Id}/transitions/{Uri.EscapeDataString(Key)}",
        new ConfigureTransitionRequest(Version,FromRoleId,FromStatus,ToRoleId,ToStatus,RequireCommit,RequirePassingTests,RequireApproval,RequiredArtifact,Enabled),ct,true);
}
public sealed record TransitionEditor(WorkspaceCatalog Catalog, WorkflowView Flow, WorkflowTransitionView? Transition);
