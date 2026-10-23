using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Opt-in supports only the trusted Codex bridge. Other engines stay disabled;
// health of Hermes/OpenCode must not silently enable a host-shell executor.
public sealed class CodexGatewaySandbox(IGatewayBindings bindings) : IGatewaySandbox
{
    private CodexBridgeAdapter Adapter(GatewayBinding binding) => bindings.Adapter(binding) as CodexBridgeAdapter
        ?? throw new ExternalAgentException("gateway-sandbox-unavailable");
    public Task VerifyReadyAsync(GatewayBinding binding, CancellationToken ct) => Adapter(binding).VerifyReadyAsync(binding, ct);
    public Task<GatewaySandboxEvidence> CollectAsync(GatewayBinding binding, ExternalAgentState nativeState, CancellationToken ct)
        => Adapter(binding).CollectAsync(binding, nativeState, ct);
}
