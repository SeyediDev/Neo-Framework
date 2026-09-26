using Neo.AgentOrchestration.Domain.Projects;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Agents;

// Configuration only. An execution's status, lease, output and credentials do
// not belong to a profile and will be held by AgentRun / the harness secret store.
public sealed class AgentProfile : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid RoleProfileId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string? Model { get; private set; }
    public string? Instructions { get; private set; }
    public string? SkillPath { get; private set; }
    public bool IsEnabled { get; private set; }

    public AgentProfile() { }

    public static AgentProfile Create(WorkspaceScope scope, RoleProfile role, string key,
        string name, string provider, string? model = null, string? instructions = null, string? skillPath = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        role.RequireScope(scope);
        if (!role.IsEnabled) throw new InvalidOperationException("Role is disabled.");
        var profile = new AgentProfile
        {
            Id = Guid.NewGuid(), OrganizationId = role.OrganizationId, WorkspaceId = role.WorkspaceId,
            RoleProfileId = ProjectRules.Id(role.Id), Key = ProjectRules.Key(key), IsEnabled = true
        };
        profile.Update(scope, name, provider, model, instructions, skillPath);
        return profile;
    }

    public void Update(WorkspaceScope scope, string name, string provider, string? model,
        string? instructions, string? skillPath)
    {
        RequireScope(scope);
        // Validate all input first so a failed update cannot partially mutate a tracked entity.
        var validName = ProjectRules.Name(name);
        var validProvider = ProjectRules.Key(provider).ToLowerInvariant();
        var validModel = ProfileRules.Text(model, 200);
        var validInstructions = ProfileRules.Text(instructions, 32000);
        var validPath = ProfileRules.SkillPath(skillPath);
        Name = validName;
        Provider = validProvider;
        Model = validModel;
        Instructions = validInstructions;
        SkillPath = validPath;
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
