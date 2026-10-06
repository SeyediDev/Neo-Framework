namespace Neo.AgentOrchestration.Contracts;

public sealed record WorkspaceView(Guid OrganizationId, Guid Id, string Key, string Name);
public sealed record AccessibleWorkspaceView(Guid OrganizationId, Guid Id, string Key, string Name);
public sealed record AccessibleOrganizationView(Guid Id, string Key, string Name, IReadOnlyList<AccessibleWorkspaceView> Workspaces);
public sealed record AccessibleWorkspaceCatalog(IReadOnlyList<AccessibleOrganizationView> Organizations);
public sealed record ProjectView(Guid Id, string Key, string Name, bool IsEnabled,
    ProjectRepositoryBindingView? RepositoryBinding = null);
public sealed record WorkspaceCatalog(WorkspaceView Workspace, IReadOnlyList<ProjectView> Projects,
    IReadOnlyList<RoleProfileView> Roles, IReadOnlyList<AgentProfileView> Agents, IReadOnlyList<WorkflowView> Workflows);
public sealed record CreateProjectRequest(string Key, string Name);
public sealed record RenameProjectRequest(string Name);
public sealed record CreateWorkItemRequest(Guid ProjectId, string Key, string Title, string Domain,
    string? Description = null, string Priority = "Normal", Guid? ParentWorkItemId = null, long? EstimatedSeconds = null,
    string Type = "Task", string? AcceptanceCriteria = null, Guid? RequestId = null);
public sealed record SetWorkItemPlanningRequest(Guid ExpectedVersion, string Type, string? AcceptanceCriteria = null);
public sealed record ClaimWorkItemRequest(Guid ExpectedVersion, Guid RoleId, string? Branch = null);
public sealed record ChangeStatusRequest(Guid ExpectedVersion, string Status, string? Note = null);
public sealed record AppendLogRequest(Guid ExpectedVersion, string Message);
public sealed record SetEstimateRequest(Guid ExpectedVersion, long? Seconds);
public sealed record VersionRequest(Guid ExpectedVersion);
public sealed record AddDependencyRequest(Guid ExpectedVersion, Guid DependsOnWorkItemId);
public sealed record AddEvidenceRequest(Guid ExpectedVersion, string Kind, string Reference, string Outcome,
    string? Details = null, string? CommitSha = null);
public sealed record WorkBoard(IReadOnlyList<WorkItemView> Items, int Total, int Skip, int Take, WorkBoardMetrics Metrics);
public sealed record WorkBoardMetrics(long ElapsedSeconds, long EstimatedSeconds, int TrackingCount,
    IReadOnlyDictionary<string, int> StatusCounts);
public sealed record CreateWorkflowRequest(Guid ProjectId, string Key, string Name);
public sealed record UpdateWorkflowRequest(Guid ExpectedVersion, string Name, bool IsEnabled);
public sealed record ConfigureTransitionRequest(Guid ExpectedVersion, Guid FromRoleId, string FromStatus,
    Guid? ToRoleId, string ToStatus, bool RequireCommit = false, bool RequirePassingTests = false,
    bool RequireApproval = false, string? RequiredArtifact = null, bool IsEnabled = true);
public sealed record PreviewWorkflowRequest(Guid WorkItemId, Guid TransitionId, Guid? PreferredAgentProfileId = null);
public sealed record ApproveWorkflowRequest(Guid ExpectedWorkflowVersion, Guid WorkItemId, Guid ExpectedWorkItemVersion,
    Guid TransitionId, bool Approved, string Reason);
public sealed record WorkflowApprovalView(Guid Id, Guid WorkflowId, Guid WorkflowVersion, Guid WorkItemId,
    Guid WorkItemVersion, Guid TransitionId, string ReviewerAgentId, string ReviewerChatId,
    bool Approved, string Reason, DateTimeOffset CreatedAtUtc);
