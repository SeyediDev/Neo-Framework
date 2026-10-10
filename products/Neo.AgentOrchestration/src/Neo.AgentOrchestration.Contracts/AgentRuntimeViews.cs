namespace Neo.AgentOrchestration.Contracts;

// Installation observations are NOT task acceptance, execution authority or usage.
public sealed record AgentRuntimeView(string Engine, string? Version, bool Installed,
    bool TransportHealthy, bool ExecutionReady, string Reason, DateTimeOffset? ObservedAtUtc);
public sealed record AgentRuntimeCatalog(Guid ProjectId, IReadOnlyList<AgentRuntimeView> Runtimes);
