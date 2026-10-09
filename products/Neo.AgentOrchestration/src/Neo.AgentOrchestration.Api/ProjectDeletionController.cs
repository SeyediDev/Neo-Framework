using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Api;

[Authorize(Policy = WorkspaceSecurity.Configure)]
public sealed class ProjectDeletionController(ISender sender, ILogger<ProjectDeletionController> logger)
    : WorkspaceControllerBase(sender)
{
    [HttpGet("projects/{id:guid}/deletion-preview")]
    public Task<ProjectDeletionPreview> Preview(Guid id, CancellationToken ct)
        => Sender.Send(new PreviewProjectDeletion(Scope, id), ct);

    [HttpDelete("projects/{id:guid}"), Authorize(Policy = WorkspaceSecurity.Write)]
    public async Task<ProjectDeletionResult> Delete(Guid id, DeleteProjectRequest body, CancellationToken ct)
    {
        var actor = Actor();
        var result = await Sender.Send(new DeleteProject(Scope, id, actor, body), ct);
        // Operational audit, not a surviving copy of deleted task content.
        logger.LogInformation("ProjectDeleted {OrganizationId} {WorkspaceId} {ProjectId} by {Subject} {ChatId}; {WorkItems} work items",
            Scope.OrganizationId, Scope.WorkspaceId, id, actor.AgentId, actor.ChatId, result.DeletedRecords["WorkItems"]);
        return result;
    }
}
