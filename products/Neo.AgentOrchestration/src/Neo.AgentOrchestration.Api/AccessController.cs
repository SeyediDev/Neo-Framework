using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Api;

[ApiController]
[Route("api/orchestration/v1/access")]
[Authorize]
public sealed class AccessController(IAccessibleWorkspaceCatalog catalog) : ControllerBase
{
    [HttpGet("workspaces")]
    public async Task<AccessibleWorkspaceCatalog> Workspaces(CancellationToken ct)
    {
        var grants = User.FindAll("nao_grant")
            .Select(x => x.Value.Split('/', StringSplitOptions.TrimEntries))
            .Where(x => x.Length == 3 && string.Equals(x[2], "read", StringComparison.OrdinalIgnoreCase))
            .Select(x => Guid.TryParse(x[0], out var organization) && Guid.TryParse(x[1], out var workspace)
                ? new WorkspaceScope(organization, workspace) : null)
            .Where(x => x is not null).Select(x => x!).ToArray();
        var organizations = await catalog.GetAsync(grants, ct);
        return new(organizations.Select(x => new AccessibleOrganizationView(x.Id, x.Key, x.Name,
            x.Workspaces.Select(w => new AccessibleWorkspaceView(w.OrganizationId, w.Id, w.Key, w.Name)).ToArray())).ToArray());
    }
}
