using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Projects;

public sealed class Workspace : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsEnabled { get; private set; }

    public Workspace() { }

    public static Workspace Create(Organization organization, string key, string name)
    {
        ArgumentNullException.ThrowIfNull(organization);
        if (!organization.IsEnabled) throw new InvalidOperationException("Organization is disabled.");
        return new Workspace
        {
            Id = Guid.NewGuid(), OrganizationId = ProjectRules.Id(organization.Id),
            Key = ProjectRules.Key(key), Name = ProjectRules.Name(name), IsEnabled = true
        };
    }

    public WorkspaceScope Scope => new(OrganizationId, Id);
    public void Rename(string name) => Name = ProjectRules.Name(name);
    public void Disable() => IsEnabled = false;
}
