using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Application.Workspace;

public interface IAccessibleWorkspaceCatalog
{
    Task<IReadOnlyList<OrganizationAccess>> GetAsync(IReadOnlyCollection<WorkspaceScope> grants, CancellationToken ct);
}

public sealed record OrganizationAccess(Guid Id, string Key, string Name, IReadOnlyList<WorkspaceAccess> Workspaces);
public sealed record WorkspaceAccess(Guid OrganizationId, Guid Id, string Key, string Name);
