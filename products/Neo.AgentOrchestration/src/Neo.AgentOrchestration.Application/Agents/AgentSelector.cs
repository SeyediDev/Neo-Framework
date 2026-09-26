using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Application.Agents;

public static class AgentSelector
{
    // The caller must obtain scope from authenticated membership and load a
    // workspace-filtered candidate set. This additional check prevents accidental
    // selection of a profile from an unrelated role or workspace.
    public static AgentProfile Select(WorkspaceScope scope, RoleProfile role,
        IEnumerable<AgentProfile> profiles, Guid? preferredProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(profiles);
        role.RequireScope(scope);
        if (!role.IsEnabled) throw new InvalidOperationException("Role is disabled.");
        if (preferredProfileId == Guid.Empty) throw new ArgumentException("Profile identifier is empty.", nameof(preferredProfileId));

        var candidates = profiles.Where(p => p.IsEnabled && p.RoleProfileId == role.Id &&
            p.OrganizationId == scope.OrganizationId && p.WorkspaceId == scope.WorkspaceId &&
            (!preferredProfileId.HasValue || p.Id == preferredProfileId.Value)).Take(2).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("No enabled agent profile matches this role and workspace."),
            _ => throw new InvalidOperationException("Several agent profiles match; choose an explicit profile.")
        };
    }
}
