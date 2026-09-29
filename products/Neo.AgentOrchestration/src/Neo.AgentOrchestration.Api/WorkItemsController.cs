using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Api;

public sealed class WorkItemsController(ISender sender) : WorkspaceControllerBase(sender)
{
    [HttpGet("items")]
    public Task<WorkBoard> Board(CancellationToken ct, [FromQuery] Guid? projectId = null, [FromQuery] string? domain = null,
        [FromQuery] Guid? roleId = null, [FromQuery] string? status = null, [FromQuery] bool includeArchived = false,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, [FromQuery] string? type = null)
        => Sender.Send(new GetWorkBoard(Scope, projectId, domain, roleId, status is null ? null : Value<WorkItemStatus>(status),
            includeArchived, skip, take, type is null ? null : Value<WorkItemType>(type)), ct);

    [HttpGet("items/{id:guid}")]
    public Task<WorkItemDetails> Details(Guid id, CancellationToken ct) => Sender.Send(new GetWorkItem(Scope, id), ct);

    [HttpPost("items"), Authorize(Policy = WorkspaceSecurity.Write)]
    [ProducesResponseType<WorkItemDetails>(201)]
    public async Task<ActionResult<WorkItemDetails>> Create(CreateWorkItemRequest body, CancellationToken ct)
    {
        var value = await Sender.Send(new CreateWorkItem(Scope, body.ProjectId, body.Key, body.Title, body.Domain, Actor(),
            body.Description, Value<WorkItemPriority>(body.Priority), body.ParentWorkItemId, body.EstimatedSeconds,
            Value<WorkItemType>(body.Type), body.AcceptanceCriteria), ct);
        return CreatedAtAction(nameof(Details), new { organizationId = Scope.OrganizationId, workspaceId = Scope.WorkspaceId, id = value.Item.Id }, value);
    }

    [HttpPost("items/{id:guid}/claim"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Claim(Guid id, ClaimWorkItemRequest body, CancellationToken ct)
        => Sender.Send(new ClaimWorkItem(Scope, id, body.RoleId, Actor(), body.ExpectedVersion, body.Branch), ct);

    [HttpPut("items/{id:guid}/planning"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Planning(Guid id, SetWorkItemPlanningRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new PlanningChange(Value<WorkItemType>(body.Type), body.AcceptanceCriteria), ct);

    [HttpPost("items/{id:guid}/status"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Status(Guid id, ChangeStatusRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new StatusChange(Value<WorkItemStatus>(body.Status), body.Note), ct);
    [HttpPost("items/{id:guid}/logs"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Log(Guid id, AppendLogRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new LogChange(body.Message), ct);
    [HttpPut("items/{id:guid}/estimate"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Estimate(Guid id, SetEstimateRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new EstimateChange(body.Seconds), ct);
    [HttpPost("items/{id:guid}/time/start"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Start(Guid id, VersionRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new TrackingChange(true), ct);
    [HttpPost("items/{id:guid}/time/stop"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Stop(Guid id, VersionRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new TrackingChange(false), ct);
    [HttpPost("items/{id:guid}/archive"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Archive(Guid id, VersionRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new ArchiveChange(true), ct);
    [HttpPost("items/{id:guid}/restore"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Restore(Guid id, VersionRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new ArchiveChange(false), ct);
    [HttpPost("items/{id:guid}/dependencies"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Dependency(Guid id, AddDependencyRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new DependencyChange(body.DependsOnWorkItemId), ct);
    [HttpPost("items/{id:guid}/evidence"), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<WorkItemDetails> Evidence(Guid id, AddEvidenceRequest body, CancellationToken ct)
        => Change(id, body.ExpectedVersion, new EvidenceChange(Value<EvidenceKind>(body.Kind), body.Reference,
            Value<EvidenceOutcome>(body.Outcome), body.Details, body.CommitSha), ct);

    private Task<WorkItemDetails> Change(Guid id, Guid version, WorkItemChange change, CancellationToken ct)
        => Sender.Send(new UpdateWorkItem(Scope, id, Actor(), version, change), ct);
}
