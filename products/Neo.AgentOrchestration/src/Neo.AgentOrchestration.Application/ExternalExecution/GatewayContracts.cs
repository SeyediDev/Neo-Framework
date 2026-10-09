using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.ExternalExecution;

namespace Neo.AgentOrchestration.Application.ExternalExecution;

// Resolved by operator configuration, never from task text, model, endpoint or
// a client-submitted directory. Fingerprint includes sandbox/repository revision.
public sealed record GatewayBinding(string Key, string Fingerprint, string SandboxId,
    ExternalAgentScope Scope, Guid AgentProfileId, Uri CallbackUrl);
public interface IGatewayBindings
{
    GatewayBinding Resolve(string key, ExternalAgentScope scope, Guid profileId, string callbackUrl);
    IExternalAgentAdapter Adapter(GatewayBinding binding);
}
public interface IGatewayJournal
{
    Task<GatewayRun> ReserveAsync(GatewayRun candidate, CancellationToken ct);
    Task<GatewayRun> ReadAsync(ExternalAgentScope scope, CancellationToken ct);
    Task<GatewayRun> AcquireAsync(ExternalAgentScope scope, Guid lease, DateTimeOffset now, CancellationToken ct);
    Task<GatewayRun> ChangeAsync(ExternalAgentScope scope, Guid expectedVersion,
        Action<GatewayRun> change, CancellationToken ct);
    // Stable immutable per-message usage. Conflict on changed retry, not sum.
    Task SaveUsageAsync(ExternalAgentScope scope, Guid lease, DateTimeOffset now,
        IReadOnlyList<ExternalAgentUsage> reports, CancellationToken ct);
}
public sealed record GatewaySandboxEvidence(bool ExecutorExited, HarnessResult? Result);
public interface IGatewaySandbox
{
    // Must prove this exact reserved non-root sandbox is isolated and matches
    // the native binding. No default host-shell execution implementation.
    Task VerifyReadyAsync(GatewayBinding binding, CancellationToken ct);
    Task<GatewaySandboxEvidence> CollectAsync(GatewayBinding binding, ExternalAgentState nativeState, CancellationToken ct);
}
public interface IGatewayResultDelivery
{
    // Result must be transmitted byte-for-byte from the durable frozen journal;
    // callback authority and secret are not taken from the task snapshot.
    Task<Guid> DeliverAsync(GatewayBinding binding, string frozenResult, CancellationToken ct);
}
