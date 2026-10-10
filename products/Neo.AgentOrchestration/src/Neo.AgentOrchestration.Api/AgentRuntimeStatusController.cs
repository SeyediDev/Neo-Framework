using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Api;

public sealed class AgentRuntimeStatusController(ISender sender) : WorkspaceControllerBase(sender)
{
    [HttpGet("projects/{projectId:guid}/agent-runtimes"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public async Task<AgentRuntimeCatalog> Get(Guid projectId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var catalog = await Sender.Send(new GetWorkspaceCatalog(Scope), ct);
        if (!catalog.Projects.Any(x => x.Id == projectId)) throw new KeyNotFoundException();
        return await Sender.Send(new GetAgentRuntimeStatus(Scope, projectId), ct);
    }
}
