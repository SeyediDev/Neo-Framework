namespace Neo.AgentOrchestration.Contracts;

public sealed record WorkflowView(Guid Id, Guid ProjectId, string Key, string Name, bool IsEnabled,
    Guid Version, IReadOnlyList<WorkflowTransitionView> Transitions);
public sealed record WorkflowTransitionView(Guid Id, string Key, Guid FromRoleId, string FromStatus,
    Guid? ToRoleId, string ToStatus, bool RequireCommit, bool RequirePassingTests,
    bool RequireApproval, string? RequiredArtifact, bool IsEnabled);
public sealed record WorkflowPlan(Guid WorkflowId, Guid WorkflowVersion, Guid TransitionId,
    Guid WorkItemId, Guid WorkItemVersion, string TargetStatus, Guid? TargetRoleId,
    Guid? AgentProfileId, bool GatesSatisfied, IReadOnlyList<string> UnmetConditions);
