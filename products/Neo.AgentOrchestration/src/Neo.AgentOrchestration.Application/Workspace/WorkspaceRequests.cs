using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Application.Workspace;

public sealed record GetWorkspaceCatalog(WorkspaceScope Scope) : IRequest<WorkspaceCatalog>;
public sealed record ConfigureWorkspace(WorkspaceScope Scope, WorkspaceChange Change) : IRequest<WorkspaceCatalog>;
public abstract record WorkspaceChange;
public sealed record NewProject(CreateProjectRequest Value) : WorkspaceChange;
public sealed record RenameProject(Guid Id, string Name) : WorkspaceChange;
public sealed record DisableProject(Guid Id) : WorkspaceChange;
public sealed record NewRole(CreateRoleProfileRequest Value) : WorkspaceChange;
public sealed record EditRole(Guid Id, UpdateRoleProfileRequest Value) : WorkspaceChange;
public sealed record EnableRole(Guid Id, bool Enabled) : WorkspaceChange;
public sealed record NewAgent(CreateAgentProfileRequest Value) : WorkspaceChange;
public sealed record EditAgent(Guid Id, UpdateAgentProfileRequest Value) : WorkspaceChange;
public sealed record EnableAgent(Guid Id, bool Enabled) : WorkspaceChange;
public sealed record NewWorkflow(CreateWorkflowRequest Value) : WorkspaceChange;
public sealed record EditWorkflow(Guid Id, UpdateWorkflowRequest Value) : WorkspaceChange;
public sealed record ConfigureTransition(Guid WorkflowId, Guid ExpectedVersion, string Key,
    Guid FromRoleId, WorkItemStatus FromStatus, Guid? ToRoleId, WorkItemStatus ToStatus,
    WorkflowGates Gates, bool Enabled) : WorkspaceChange;
public sealed record GetWorkBoard(WorkspaceScope Scope, Guid? ProjectId = null, string? Domain = null,
    Guid? RoleId = null, WorkItemStatus? Status = null, bool IncludeArchived = false, int Skip = 0, int Take = 50) : IRequest<WorkBoard>;
public sealed record PreviewWorkflow(WorkspaceScope Scope, Guid WorkflowId, PreviewWorkflowRequest Value) : IRequest<WorkflowPlan>;
public sealed record ApproveWorkflow(WorkspaceScope Scope, Guid WorkflowId, WorkActor Reviewer,
    ApproveWorkflowRequest Value) : IRequest<WorkflowApprovalView>;
public sealed record GetWorkflowApprovals(WorkspaceScope Scope, Guid WorkflowId, Guid WorkItemId) : IRequest<IReadOnlyList<WorkflowApprovalView>>;
public sealed class OrchestrationUnavailableException() : Exception("Orchestration storage is not configured.");
