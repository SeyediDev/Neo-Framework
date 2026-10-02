using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Application.Runs;

public interface IRunProviders
{
    void RequireAvailable(string provider, WorkspaceScope scope);
    HarnessBinding Bind(string provider, WorkspaceScope scope, Guid runId);
}
public sealed record HarnessBinding(string Fingerprint, string CallbackUrl, bool CompactContextOptIn = false);
public sealed class RunProviderUnavailableException(string provider) : InvalidOperationException("Execution provider is unavailable.")
{
    public string Code => provider == "fake" ? "simulation-disabled" : "harness-unavailable";
}
// Direct application tests can opt into pure simulation without infrastructure.
// API and Worker explicitly register their configuration-backed policy.
public sealed class SimulationRunProviders : IRunProviders
{
    public void RequireAvailable(string provider, WorkspaceScope scope)
    { if (provider != "fake") throw new RunProviderUnavailableException(provider); }
    public HarnessBinding Bind(string provider, WorkspaceScope scope, Guid runId) => throw new RunProviderUnavailableException(provider);
}
