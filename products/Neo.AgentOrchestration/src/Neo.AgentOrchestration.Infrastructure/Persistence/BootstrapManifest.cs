using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

// Operator-owned, credential-free bootstrap input, not exported host settings.
public sealed record BootstrapEntry(string Key, string Name);
public sealed record BootstrapRole(string Key, string Name, string? ScopeDescription = null);
public sealed record BootstrapManifest(int SchemaVersion, string Database, BootstrapEntry Organization,
    BootstrapEntry Workspace, BootstrapEntry Project, IReadOnlyList<BootstrapRole> Roles)
{
    public static BootstrapManifest Template(string database) => new(1, database,
        new("my-org", "My organization"), new("main", "Main workspace"), new("my-project", "My project"),
        [new("developer", "Developer", "Implement assigned work in the agreed file scope."),
         new("reviewer", "Reviewer", "Independently review evidence and requested changes.")]);

    internal (Organization Organization, Workspace Workspace, Project Project, RoleProfile[] Roles) Validate(string database)
    {
        if (SchemaVersion != 1 || Database != database || Organization is null || Workspace is null ||
            Project is null || Roles is null || Roles.Count is < 1 or > 50 || Roles.Any(x => x is null))
            throw new ArgumentException("Invalid bootstrap manifest.");
        var org = Domain.Projects.Organization.Create(Organization.Key, Organization.Name);
        var workspace = Domain.Projects.Workspace.Create(org, Workspace.Key, Workspace.Name);
        var project = Domain.Projects.Project.Create(workspace, Project.Key, Project.Name);
        var roles = Roles.Select(x => RoleProfile.Create(workspace, x.Key, x.Name, x.ScopeDescription)).ToArray();
        if (roles.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new ArgumentException("Duplicate role keys.");
        return (org, workspace, project, roles);
    }
}

public sealed record BootstrapResult(Guid OrganizationId, Guid WorkspaceId, Guid ProjectId,
    IReadOnlyDictionary<string, Guid> RoleIds, int CreatedRecords);
public sealed record DatabaseHealth(bool Ready, string Reason, int AppliedMigrations, int ExpectedMigrations);
public sealed class BootstrapConflictException() : Exception("Existing bootstrap configuration differs or is disabled.");
