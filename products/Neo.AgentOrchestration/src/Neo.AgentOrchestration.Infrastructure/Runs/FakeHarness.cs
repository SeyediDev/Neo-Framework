using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Domain.Runs;

namespace Neo.AgentOrchestration.Infrastructure.Runs;

// Deterministic and side-effect free: no shell, repository edits, API, model or
// provider call. Never fabricate commits/tests/artifacts to satisfy a real gate.
public sealed class FakeHarness : ISimulationHarness
{
    public SimulationResult Result(AgentRun run)
    {
        if (run.Provider != "fake") throw new InvalidOperationException("Not a simulation profile.");
        return new(run.SimulationOutcome,
            $"FakeHarness simulation: {run.SimulationOutcome}. No repository changes or external agent execution occurred.");
    }
}
