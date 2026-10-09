namespace Neo.AgentOrchestration.Application.ExternalAgents;

// Native protocols sit behind the durable harness gateway, not workspace CRUD
// or API startup. These operations neither claim a task nor complete a workflow.
public enum ExternalAgentEngine { Hermes, OpenCode }
public enum ExternalAgentState { Unknown, Queued, Running, AwaitingApproval, StopRequested, Completed, Failed, Cancelled, Interrupted }
public sealed record ExternalAgentScope(Guid OrganizationId, Guid WorkspaceId, Guid ProjectId, Guid RunId);
public sealed record ExternalAgentInput(ExternalAgentScope Scope, string Prompt, string? Instructions = null);
// Persist preparation BEFORE submission and returned native identity BEFORE any
// observation/recovery. The caller is responsible for durable body deduplication.
public sealed record ExternalAgentHandle(ExternalAgentScope Scope, ExternalAgentEngine Engine,
    string BindingFingerprint, string PromptId, string? NativeId = null);
public sealed record ExternalAgentUsage(string ReportId, string Provider, string Model,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens);
// Immutable completed-message reports, not cumulative snapshots to add at every
// poll. Unknown counters stay null. Neither progress nor trusted billing.
public sealed record ExternalAgentObservation(ExternalAgentState State,
    IReadOnlyList<ExternalAgentUsage> Usage, string? NativeApprovalId = null);

public interface IExternalAgentAdapter
{
    ExternalAgentEngine Engine { get; }
    string BindingFingerprint { get; }
    Task<ExternalAgentHandle> PrepareAsync(ExternalAgentInput input, CancellationToken cancellationToken);
    Task<ExternalAgentHandle> SubmitAsync(ExternalAgentInput input, ExternalAgentHandle prepared, CancellationToken cancellationToken);
    Task<ExternalAgentObservation> ObserveAsync(ExternalAgentHandle handle, CancellationToken cancellationToken);
    Task RequestStopAsync(ExternalAgentHandle handle, CancellationToken cancellationToken);
}

// Stable codes only: no upstream bodies, paths, credentials or exception chains.
// A failed/lost response to a write may still mean that execution has started.
public sealed class ExternalAgentException(string code, bool requiresReconciliation = false)
    : InvalidOperationException("External agent operation could not be verified.")
{
    public string Code { get; } = code;
    public bool RequiresReconciliation { get; } = requiresReconciliation;
}
