using MediatR;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workflows;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Application.Workspace;

public sealed class WorkspaceHandlers(IWorkspaceWorkStore store, TimeProvider clock) :
    IRequestHandler<GetWorkspaceCatalog, WorkspaceCatalog>, IRequestHandler<ConfigureWorkspace, WorkspaceCatalog>,
    IRequestHandler<GetWorkBoard, WorkBoard>, IRequestHandler<PreviewWorkflow, WorkflowPlan>,
    IRequestHandler<ApproveWorkflow, WorkflowApprovalView>, IRequestHandler<GetWorkflowApprovals, IReadOnlyList<WorkflowApprovalView>>
{
    public Task<WorkspaceCatalog> Handle(GetWorkspaceCatalog r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, Catalog, ct);

    public async Task<WorkspaceCatalog> Handle(ConfigureWorkspace r, CancellationToken ct)
    {
        await store.ExecuteAsync(r.Scope, async (s, token) =>
        {
            switch (r.Change)
            {
                case NewProject change:
                    var project = Domain.Projects.Project.Create(await s.GetWorkspaceAsync(token), change.Value.Key, change.Value.Name);
                    Unique((await s.GetProjectsAsync(token)).Any(x => x.Key == project.Key)); s.Add(project); break;
                case RenameProject change:
                    (await Project(s, change.Id, token)).Rename(r.Scope, change.Name); break;
                case UpsertRepositoryBinding change:
                    var repositoryProject = await Project(s, change.ProjectId, token);
                    var existingBinding = await s.GetRepositoryBindingAsync(repositoryProject.Id, token);
                    if (existingBinding is null) s.Add(ProjectRepositoryBinding.Create(r.Scope, repositoryProject, change.Value.Provider, change.Value.RepositoryUrl, change.Value.RepositoryKey, change.Value.DefaultBranch, change.Value.DevelopmentBranch, change.Value.CiCdReference, change.Value.SecretReference, clock.GetUtcNow()));
                    else existingBinding.Update(r.Scope, change.Value.Provider, change.Value.RepositoryUrl, change.Value.RepositoryKey, change.Value.DefaultBranch, change.Value.DevelopmentBranch, change.Value.CiCdReference, change.Value.SecretReference, change.Value.IsEnabled, clock.GetUtcNow()); break;
                case DisableProject change:
                    (await Project(s, change.Id, token)).Disable(r.Scope); break;
                case NewRole change:
                    var role = RoleProfile.Create(await s.GetWorkspaceAsync(token), change.Value.Key, change.Value.Name, change.Value.ScopeDescription, change.Value.MaxConcurrentWorkItems);
                    Unique((await s.GetRolesAsync(token)).Any(x => x.Key == role.Key)); s.Add(role); break;
                case EditRole change:
                    (await Role(s, change.Id, token)).Update(r.Scope, change.Value.Name, change.Value.ScopeDescription, change.Value.MaxConcurrentWorkItems); break;
                case EnableRole change:
                    (await Role(s, change.Id, token)).SetEnabled(r.Scope, change.Enabled); break;
                case NewAgent change:
                    var a = change.Value;
                    var agent = AgentProfile.Create(r.Scope, await Role(s, a.RoleProfileId, token), a.Key, a.Name, a.Provider, a.Model, a.Instructions, a.SkillPath);
                    Unique((await s.GetAgentsAsync(token)).Any(x => x.Key == agent.Key)); s.Add(agent); break;
                case EditAgent change:
                    var b = change.Value;
                    (await Agent(s, change.Id, token)).Update(r.Scope, b.Name, b.Provider, b.Model, b.Instructions, b.SkillPath); break;
                case EnableAgent change:
                    (await Agent(s, change.Id, token)).SetEnabled(r.Scope, change.Enabled); break;
                case NewWorkflow change:
                    var flow = WorkflowDefinition.Create(r.Scope, await Project(s, change.Value.ProjectId, token), change.Value.Key, change.Value.Name);
                    Unique((await s.GetWorkflowsAsync(token)).Any(x => x.ProjectId == flow.ProjectId && x.Key == flow.Key)); s.Add(flow); break;
                case EditWorkflow change:
                    var editable = await Workflow(s, change.Id, token);
                    Version(editable.Version, change.Value.ExpectedVersion);
                    editable.Update(r.Scope, change.Value.Name, change.Value.IsEnabled); break;
                case ConfigureTransition change:
                    var workflow = await Workflow(s, change.WorkflowId, token);
                    Version(workflow.Version, change.ExpectedVersion);
                    workflow.ConfigureTransition(r.Scope, change.Key, await Role(s, change.FromRoleId, token), change.FromStatus,
                        change.ToRoleId.HasValue ? await Role(s, change.ToRoleId.Value, token) : null,
                        change.ToStatus, change.Gates, change.Enabled); break;
                default: throw new ArgumentException("Unknown configuration change.");
            }
            // New tracked rows are not visible to SQL queries until the owning
            // store commits. Return after that commit using a fresh read below.
            return true;
        }, ct);
        return await Handle(new GetWorkspaceCatalog(r.Scope), ct);
    }

    public Task<WorkBoard> Handle(GetWorkBoard r, CancellationToken ct)
    {
        if (r.Skip < 0 || r.Take is < 1 or > 200 || r.Domain?.Length > 80 ||
            (r.Status.HasValue && !Enum.IsDefined(r.Status.Value)) || (r.Type.HasValue && !Enum.IsDefined(r.Type.Value)))
            throw new ArgumentException("Invalid board filter or page size.");
        return store.ExecuteAsync(r.Scope, async (s, token) =>
        {
            var projects = await s.GetProjectsAsync(token);
            if (r.ProjectId.HasValue && !projects.Any(x => x.Id == r.ProjectId)) throw new KeyNotFoundException("Project not found.");
            if (r.RoleId.HasValue) _ = await Role(s, r.RoleId.Value, token);
            var items = new List<WorkItem>();
            foreach (var project in projects.Where(x => !r.ProjectId.HasValue || x.Id == r.ProjectId))
                items.AddRange(await s.GetProjectItemsAsync(project.Id, token));
            var now = clock.GetUtcNow();
            var filtered = items.Where(x => (r.IncludeArchived || !x.IsArchived) &&
                (r.Domain is null || string.Equals(x.Domain, r.Domain.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                (!r.RoleId.HasValue || x.OwnerRoleId == r.RoleId) && (!r.Status.HasValue || x.Status == r.Status) &&
                (!r.Type.HasValue || x.Type == r.Type))
                .OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id).Select(x => WorkItemProjection.View(x, now)).ToArray();
            return new WorkBoard(filtered.Skip(r.Skip).Take(r.Take).ToArray(), filtered.Length, r.Skip, r.Take,
                new WorkBoardMetrics(filtered.Sum(x => x.ElapsedSeconds), filtered.Sum(x => x.EstimatedSeconds ?? 0),
                    filtered.Count(x => x.IsTracking), filtered.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count())));
        }, ct);
    }

    public Task<WorkflowPlan> Handle(PreviewWorkflow r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (s, token) =>
        {
            var flow = await Workflow(s, r.WorkflowId, token);
            var item = await Item(s, r.Value.WorkItemId, token);
            return WorkflowPlanner.Preview(r.Scope, flow, r.Value.TransitionId, item, await s.GetRolesAsync(token),
                await s.GetAgentsAsync(token), await s.GetApprovalsAsync(flow.Id, item.Id, token), clock.GetUtcNow(), r.Value.PreferredAgentProfileId);
        }, ct);

    public Task<WorkflowApprovalView> Handle(ApproveWorkflow r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (s, token) =>
        {
            var flow = await Workflow(s, r.WorkflowId, token); var item = await Item(s, r.Value.WorkItemId, token);
            Version(flow.Version, r.Value.ExpectedWorkflowVersion); Version(item.Version, r.Value.ExpectedWorkItemVersion);
            // A managed run has a synthetic owner. Its initiating user must not
            // gain an independent-review loophole through that identity change.
            if (s is Neo.AgentOrchestration.Application.Runs.IRunSession runs &&
                (await runs.GetRunsAsync(item.Id, token)).Any(x => x.Actor.AgentId == item.OwnerAgentId &&
                    x.RequestedByAgentId == r.Reviewer.AgentId))
                throw new WorkItemConflictException("The execution requestor cannot independently approve its own run.");
            var approval = WorkflowApproval.Record(r.Scope, flow, r.Value.TransitionId, item, r.Reviewer,
                r.Value.Approved, r.Value.Reason, clock.GetUtcNow());
            s.Add(approval); return Approval(approval);
        }, ct);

    public Task<IReadOnlyList<WorkflowApprovalView>> Handle(GetWorkflowApprovals r, CancellationToken ct)
        => store.ExecuteAsync<IReadOnlyList<WorkflowApprovalView>>(r.Scope, async (s, token) =>
        {
            var flow = await Workflow(s, r.WorkflowId, token); var item = await Item(s, r.WorkItemId, token);
            if (flow.ProjectId != item.ProjectId) throw new KeyNotFoundException("Work item not found in workflow project.");
            return (await s.GetApprovalsAsync(flow.Id, item.Id, token)).Select(Approval).ToArray();
        }, ct);

    private static async Task<WorkspaceCatalog> Catalog(IWorkItemSession s, CancellationToken ct)
    {
        var w = await s.GetWorkspaceAsync(ct);
        var projects = await s.GetProjectsAsync(ct);
        var projectViews = new List<ProjectView>(projects.Count);
        foreach (var project in projects)
        {
            var binding = await s.GetRepositoryBindingAsync(project.Id, ct);
            projectViews.Add(new ProjectView(project.Id, project.Key, project.Name, project.IsEnabled,
                binding is null ? null : new ProjectRepositoryBindingView(binding.Id, binding.ProjectId, binding.Provider,
                    binding.RepositoryUrl, binding.RepositoryKey, binding.DefaultBranch, binding.DevelopmentBranch,
                    binding.CiCdReference, binding.SecretReference, binding.IsEnabled, binding.UpdatedAtUtc)));
        }
        return new(new(w.OrganizationId, w.Id, w.Key, w.Name), projectViews,
            (await s.GetRolesAsync(ct)).Select(x => new RoleProfileView(x.Id, x.Key, x.Name, x.ScopeDescription, x.IsEnabled, x.MaxConcurrentWorkItems)).ToArray(),
            (await s.GetAgentsAsync(ct)).Select(x => new AgentProfileView(x.Id, x.RoleProfileId, x.Key, x.Name, x.Provider, x.Model, x.Instructions, x.SkillPath, x.IsEnabled)).ToArray(),
            (await s.GetWorkflowsAsync(ct)).Select(x => new WorkflowView(x.Id, x.ProjectId, x.Key, x.Name, x.IsEnabled, x.Version,
                x.Transitions.Select(t => new WorkflowTransitionView(t.Id, t.Key, t.FromRoleId, t.FromStatus.ToString(), t.ToRoleId,
                    t.ToStatus.ToString(), t.RequireCommit, t.RequirePassingTests, t.RequireApproval, t.RequiredArtifact, t.IsEnabled)).ToArray())).ToArray());
    }    private static WorkflowApprovalView Approval(WorkflowApproval a) => new(a.Id, a.WorkflowDefinitionId, a.WorkflowVersion,
        a.WorkItemId, a.WorkItemVersion, a.TransitionId, a.ReviewerAgentId, a.ReviewerChatId, a.Approved, a.Reason, a.CreatedAtUtc);
    private static async Task<Project> Project(IWorkItemSession s, Guid id, CancellationToken ct)
        => await s.GetProjectAsync(id, ct) ?? throw new KeyNotFoundException("Project not found.");
    private static async Task<RoleProfile> Role(IWorkItemSession s, Guid id, CancellationToken ct)
        => await s.GetRoleAsync(id, ct) ?? throw new KeyNotFoundException("Role not found.");
    private static async Task<AgentProfile> Agent(IWorkItemSession s, Guid id, CancellationToken ct)
        => (await s.GetAgentsAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Agent not found.");
    private static async Task<WorkflowDefinition> Workflow(IWorkItemSession s, Guid id, CancellationToken ct)
        => (await s.GetWorkflowsAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Workflow not found.");
    private static async Task<WorkItem> Item(IWorkItemSession s, Guid id, CancellationToken ct)
        => await s.GetItemAsync(id, ct) ?? throw new KeyNotFoundException("Work item not found.");
    private static void Unique(bool exists) { if (exists) throw new WorkItemConflictException("The key already exists in this scope."); }
    private static void Version(Guid current, Guid expected)
    { if (expected == Guid.Empty || current != expected) throw new WorkItemConflictException("Resource changed; reload before retrying."); }
}
