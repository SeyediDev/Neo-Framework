using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Application.Work;

// Persistence must run the callback atomically, serializing role claims and
// graph changes for this workspace, and roll back on error/cancellation.
// Scope must come from authorized membership. A fake store is only a test fixture.
public interface IWorkspaceWorkStore
{
    Task<T> ExecuteAsync<T>(WorkspaceScope scope,
        Func<IWorkItemSession, CancellationToken, Task<T>> operation, CancellationToken ct);
}

public interface IWorkItemSession
{
    Task<Domain.Projects.Workspace> GetWorkspaceAsync(CancellationToken ct);
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct);
    Task<ProjectRepositoryBinding?> GetRepositoryBindingAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<RoleProfile>> GetRolesAsync(CancellationToken ct);
    Task<IReadOnlyList<AgentProfile>> GetAgentsAsync(CancellationToken ct);
    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken ct);
    Task<IReadOnlyList<WorkflowApproval>> GetApprovalsAsync(Guid workflowId, Guid workItemId, CancellationToken ct);
    void Add(Project project);
    void Add(ProjectRepositoryBinding binding);
    void Add(RoleProfile role);
    void Add(AgentProfile agent);
    void Add(WorkflowDefinition workflow);
    void Add(WorkflowApproval approval);
    // All reads are scoped by the surrounding ExecuteAsync. Work items include
    // their tracked history, evidence, dependencies and time-entry collections.
    Task<Project?> GetProjectAsync(Guid id, CancellationToken ct);
    Task<RoleProfile?> GetRoleAsync(Guid id, CancellationToken ct);
    Task<WorkItem?> GetItemAsync(Guid id, CancellationToken ct);
    Task<WorkHistoryPage> GetHistoryPageAsync(Guid itemId, int skip, int take, Guid? snapshotVersion, CancellationToken ct);
    Task<IReadOnlyList<WorkItem>> GetProjectItemsAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<Neo.AgentOrchestration.Domain.Runs.TokenUsageReport>> GetItemRunUsageAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct);
    Task<bool> IsRoleBusyAsync(Guid roleId, int capacity, Guid exceptItemId, CancellationToken ct);
    void Add(WorkItem item);
}
