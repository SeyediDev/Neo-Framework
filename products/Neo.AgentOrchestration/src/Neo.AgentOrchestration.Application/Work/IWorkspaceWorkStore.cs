using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

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
    // All reads are scoped by the surrounding ExecuteAsync. Work items include
    // their tracked history, evidence, dependencies and time-entry collections.
    Task<Project?> GetProjectAsync(Guid id, CancellationToken ct);
    Task<RoleProfile?> GetRoleAsync(Guid id, CancellationToken ct);
    Task<WorkItem?> GetItemAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<WorkItem>> GetProjectItemsAsync(Guid projectId, CancellationToken ct);
    Task<bool> IsRoleBusyAsync(Guid roleId, Guid exceptItemId, CancellationToken ct);
    void Add(WorkItem item);
}
