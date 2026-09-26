namespace Neo.AgentOrchestration.Domain.Projects;

// A validated domain scope, not proof of caller authorization.
// The API/application must derive it from the authenticated membership.
public sealed record WorkspaceScope
{
    public Guid OrganizationId { get; }
    public Guid WorkspaceId { get; }

    public WorkspaceScope(Guid organizationId, Guid workspaceId)
    {
        OrganizationId = ProjectRules.Id(organizationId);
        WorkspaceId = ProjectRules.Id(workspaceId);
    }

    public void Require(Guid organizationId, Guid workspaceId)
    {
        if (OrganizationId != organizationId || WorkspaceId != workspaceId)
            throw new InvalidOperationException("The resource belongs to another workspace.");
    }
}
