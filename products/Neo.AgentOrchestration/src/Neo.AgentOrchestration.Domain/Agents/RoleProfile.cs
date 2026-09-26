using Neo.AgentOrchestration.Domain.Projects;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Agents;

public sealed class RoleProfile : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? ScopeDescription { get; private set; }
    public bool IsEnabled { get; private set; }

    public RoleProfile() { }

    public static RoleProfile Create(Workspace workspace, string key, string name, string? scopeDescription = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.IsEnabled) throw new InvalidOperationException("Workspace is disabled.");
        return new RoleProfile
        {
            Id = Guid.NewGuid(), OrganizationId = ProjectRules.Id(workspace.OrganizationId),
            WorkspaceId = ProjectRules.Id(workspace.Id), Key = ProjectRules.Key(key),
            Name = ProjectRules.Name(name), ScopeDescription = ProfileRules.Text(scopeDescription, 4000), IsEnabled = true
        };
    }

    public void Update(WorkspaceScope scope, string name, string? scopeDescription)
    {
        RequireScope(scope);
        var validName = ProjectRules.Name(name);
        var validDescription = ProfileRules.Text(scopeDescription, 4000);
        Name = validName;
        ScopeDescription = validDescription;
    }

    public void SetEnabled(WorkspaceScope scope, bool enabled)
    {
        RequireScope(scope);
        IsEnabled = enabled;
    }

    public void RequireScope(WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Require(OrganizationId, WorkspaceId);
    }
}
