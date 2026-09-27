using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Api;

public sealed class WorkspaceConfigurationController(ISender sender) : WorkspaceControllerBase(sender)
{
    [HttpGet("catalog")]
    public Task<WorkspaceCatalog> Catalog(CancellationToken ct) => Sender.Send(new GetWorkspaceCatalog(Scope), ct);

    [HttpPost("projects"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> CreateProject(CreateProjectRequest body, CancellationToken ct) => Change(new NewProject(body), ct);
    [HttpPut("projects/{id:guid}"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> Rename(Guid id, RenameProjectRequest body, CancellationToken ct) => Change(new RenameProject(id, body.Name), ct);
    [HttpPost("projects/{id:guid}/disable"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> Disable(Guid id, CancellationToken ct) => Change(new DisableProject(id), ct);

    [HttpPost("roles"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> CreateRole(CreateRoleProfileRequest body, CancellationToken ct) => Change(new NewRole(body), ct);
    [HttpPut("roles/{id:guid}"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> UpdateRole(Guid id, UpdateRoleProfileRequest body, CancellationToken ct) => Change(new EditRole(id, body), ct);
    [HttpPut("roles/{id:guid}/enabled"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> EnableRoleProfile(Guid id, SetProfileEnabledRequest body, CancellationToken ct) => Change(new EnableRole(id, body.IsEnabled), ct);

    [HttpPost("agents"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> CreateAgent(CreateAgentProfileRequest body, CancellationToken ct) => Change(new NewAgent(body), ct);
    [HttpPut("agents/{id:guid}"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> UpdateAgent(Guid id, UpdateAgentProfileRequest body, CancellationToken ct) => Change(new EditAgent(id, body), ct);
    [HttpPut("agents/{id:guid}/enabled"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> EnableAgentProfile(Guid id, SetProfileEnabledRequest body, CancellationToken ct) => Change(new EnableAgent(id, body.IsEnabled), ct);

    [HttpPost("workflows"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> CreateWorkflow(CreateWorkflowRequest body, CancellationToken ct) => Change(new NewWorkflow(body), ct);
    [HttpPut("workflows/{id:guid}"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> UpdateWorkflowDefinition(Guid id, UpdateWorkflowRequest body, CancellationToken ct) => Change(new EditWorkflow(id, body), ct);
    [HttpPut("workflows/{id:guid}/transitions/{key}"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<WorkspaceCatalog> Transition(Guid id, string key, ConfigureTransitionRequest body, CancellationToken ct)
        => Change(new ConfigureTransition(id, body.ExpectedVersion, key, body.FromRoleId, Value<WorkItemStatus>(body.FromStatus),
            body.ToRoleId, Value<WorkItemStatus>(body.ToStatus), new(body.RequireCommit, body.RequirePassingTests, body.RequireApproval, body.RequiredArtifact), body.IsEnabled), ct);

    [HttpPost("workflows/{id:guid}/preview")]
    public Task<WorkflowPlan> Preview(Guid id, PreviewWorkflowRequest body, CancellationToken ct)
        => Sender.Send(new PreviewWorkflow(Scope, id, body), ct);
    [HttpPost("workflows/{id:guid}/approvals"), Authorize(Policy = WorkspaceSecurity.Approve)]
    public Task<WorkflowApprovalView> Approve(Guid id, ApproveWorkflowRequest body, CancellationToken ct)
        => Sender.Send(new ApproveWorkflow(Scope, id, Actor(), body), ct);
    [HttpGet("workflows/{id:guid}/approvals")]
    public Task<IReadOnlyList<WorkflowApprovalView>> Approvals(Guid id, [FromQuery] Guid workItemId, CancellationToken ct)
        => Sender.Send(new GetWorkflowApprovals(Scope, id, workItemId), ct);

    private Task<WorkspaceCatalog> Change(WorkspaceChange change, CancellationToken ct)
        => Sender.Send(new ConfigureWorkspace(Scope, change), ct);
}
