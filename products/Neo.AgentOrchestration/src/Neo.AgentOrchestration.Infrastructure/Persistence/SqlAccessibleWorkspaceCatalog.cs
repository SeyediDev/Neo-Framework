using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public sealed class SqlAccessibleWorkspaceCatalog(IDbContextFactory<OrchestrationDbContext> factory) : IAccessibleWorkspaceCatalog
{
    public async Task<IReadOnlyList<OrganizationAccess>> GetAsync(IReadOnlyCollection<WorkspaceScope> grants, CancellationToken ct)
    {
        if (grants.Count == 0) return [];
        var allowed = grants.ToHashSet();
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Workspaces.AsNoTracking()
            .Where(w => w.IsEnabled && db.Organizations.Any(o => o.Id == w.OrganizationId && o.IsEnabled))
            .Join(db.Organizations.AsNoTracking().Where(o => o.IsEnabled), w => w.OrganizationId, o => o.Id,
                (w, o) => new { Organization = o, Workspace = w })
            .OrderBy(x => x.Organization.Name).ThenBy(x => x.Workspace.Name)
            .ToArrayAsync(ct);
        return rows.Where(x => allowed.Contains(new WorkspaceScope(x.Organization.Id, x.Workspace.Id)))
            .GroupBy(x => x.Organization.Id)
            .Select(group =>
            {
                var first = group.First();
                return new OrganizationAccess(first.Organization.Id, first.Organization.Key, first.Organization.Name,
                    group.Select(x => new WorkspaceAccess(x.Organization.Id, x.Workspace.Id, x.Workspace.Key, x.Workspace.Name)).ToArray());
            }).ToArray();
    }
}
