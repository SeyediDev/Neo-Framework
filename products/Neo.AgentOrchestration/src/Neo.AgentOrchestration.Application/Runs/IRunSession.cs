using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Runs;

namespace Neo.AgentOrchestration.Application.Runs;

// All methods stage in the caller's existing workspace transaction. They must
// not SaveChanges, open a nested transaction, send HTTP or enqueue a broker job.
public interface IRunSession : IWorkItemSession
{
    Task<AgentRun?> GetRunAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid workItemId, CancellationToken ct);
    Task<IReadOnlyList<RunDeliveryView>> GetRunDeliveriesAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<TokenUsageReport>> GetTokenUsageAsync(Guid runId, CancellationToken ct);
    Task<TokenUsageReport?> FindTokenUsageAsync(string idempotencyKey, CancellationToken ct);
    void Add(TokenUsageReport usage);
    void Add(AgentRun run);
    Task StageMessageAsync(WorkDeliveryRequest request, WorkDeliveryKind kind, TimeProvider clock, CancellationToken ct);
    Task<bool> StageInboxAsync(WorkDeliveryRequest request, Func<IWorkItemSession, CancellationToken, Task> mutation,
        WorkDeliveryKind? followUp, TimeProvider clock, CancellationToken ct);
}

// Deliberately a simulation-only contract. A real external harness cannot be
// invoked inside the shared database transaction; its adapter belongs to NAO-011.
public interface ISimulationHarness
{
    SimulationResult Result(AgentRun run);
}
public sealed record SimulationResult(SimulationOutcome Outcome, string Summary);
