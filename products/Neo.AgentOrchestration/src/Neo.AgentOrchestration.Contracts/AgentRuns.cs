namespace Neo.AgentOrchestration.Contracts;

public sealed record StartAgentRunRequest(Guid RequestId, Guid ExpectedWorkItemVersion, Guid WorkflowId,
    Guid ExpectedWorkflowVersion, Guid RoleId, Guid? AgentProfileId = null, string? Branch = null, string SimulationOutcome = "Succeeded",
    bool AllowExternalExecution = false);
public sealed record EvaluateAgentRunRequest(Guid RequestId, Guid ExpectedWorkItemVersion, Guid ExpectedWorkflowVersion);
public sealed record ReturnRunAssignmentRequest(Guid ExpectedWorkItemVersion);
public sealed record AgentRunView(Guid Id, Guid WorkItemId, Guid RoleId, Guid AgentProfileId, Guid WorkflowId,
    Guid InitialWorkflowVersion, Guid WorkflowVersion, Guid WorkItemVersion, Guid? PreviousRunId, Guid? NextRunId,
    int Hop, string Provider, string? Model, string? Instructions, string? SkillPath, string? Branch,
    string RequestedByAgentId, string RequestedByChatId, string Status, string Decision, string? DecisionReason,
    string SimulationOutcome, string? ResultSummary, DateTimeOffset CreatedAtUtc, DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record RecordTokenUsageRequest(Guid RequestId, string? Provider, string? Model,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens,
    string Source = "reported", string? IdempotencyKey = null, DateTimeOffset? RecordedAtUtc = null);
public sealed record TokenUsageView(Guid Id, Guid AgentRunId, Guid WorkItemId, string Provider, string? Model,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens,
    string Source, string IdempotencyKey, DateTimeOffset RecordedAtUtc);
public sealed record RunDeliveryView(Guid OperationId, long OutboxId, string Kind, string State,
    int DispatchAttempts, int ExecutionAttempts, string? Error, DateTime? RetryAtUtc);
public sealed record AgentRunDetails(AgentRunView Run, IReadOnlyList<RunDeliveryView> Deliveries,
    IReadOnlyList<TokenUsageView>? TokenUsage = null);
