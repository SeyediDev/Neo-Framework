using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Projects;

public sealed class Project : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsEnabled { get; private set; }

    public Project() { }

    public static Project Create(Workspace workspace, string key, string name)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.IsEnabled) throw new InvalidOperationException("Workspace is disabled.");
        return new Project
        {
            Id = Guid.NewGuid(), OrganizationId = ProjectRules.Id(workspace.OrganizationId),
            WorkspaceId = ProjectRules.Id(workspace.Id), Key = ProjectRules.Key(key),
            Name = ProjectRules.Name(name), IsEnabled = true
        };
    }

    public void Rename(WorkspaceScope scope, string name)
    {
        RequireScope(scope);
        Name = ProjectRules.Name(name);
    }

    public void Disable(WorkspaceScope scope)
    {
        RequireScope(scope);
        IsEnabled = false;
    }

    public void RequireScope(WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Require(OrganizationId, WorkspaceId);
    }
}
