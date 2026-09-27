using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Api;

public sealed class AgentRunsController(ISender sender, IConfiguration configuration) : WorkspaceControllerBase(sender)
{
    [HttpGet("items/{id:guid}/runs")]
    public Task<IReadOnlyList<AgentRunDetails>> List(Guid id, CancellationToken ct)
        => Sender.Send(new GetAgentRuns(Scope, id), ct);

    [HttpGet("runs/{id:guid}")]
    public Task<AgentRunDetails> Details(Guid id, CancellationToken ct)
        => Sender.Send(new GetAgentRun(Scope, id), ct);

    [HttpPost("items/{id:guid}/runs"), Authorize(Policy = WorkspaceSecurity.Execute)]
    [ProducesResponseType<AgentRunDetails>(202)]
    public async Task<ActionResult<AgentRunDetails>> Start(Guid id, StartAgentRunRequest body, CancellationToken ct)
    {
        RequireSimulation();
        var result = await Sender.Send(new StartAgentRun(Scope, id, Actor(), body), ct);
        return AcceptedAtAction(nameof(Details), new { organizationId = Scope.OrganizationId, workspaceId = Scope.WorkspaceId, id = result.Run.Id }, result);
    }

    [HttpPost("runs/{id:guid}/evaluate"), Authorize(Policy = WorkspaceSecurity.Execute)]
    [ProducesResponseType<AgentRunDetails>(202)]
    public async Task<ActionResult<AgentRunDetails>> Evaluate(Guid id, EvaluateAgentRunRequest body, CancellationToken ct)
    {
        RequireSimulation();
        var result = await Sender.Send(new EvaluateAgentRun(Scope, id, Actor(), body), ct);
        return AcceptedAtAction(nameof(Details), new { organizationId = Scope.OrganizationId, workspaceId = Scope.WorkspaceId, id }, result);
    }

    private void RequireSimulation()
    {
        if (!configuration.GetValue<bool>("Orchestration:SimulationEnabled")) throw new SimulationDisabledException();
    }

    [HttpPost("runs/{id:guid}/return-assignment"), Authorize(Policy = WorkspaceSecurity.Execute)]
    public Task<AgentRunDetails> ReturnAssignment(Guid id, ReturnRunAssignmentRequest body, CancellationToken ct)
    {
        // Recovery does not execute anything and remains available when the
        // installation operator disables further simulation dispatch.
        return Sender.Send(new ReturnRunAssignment(Scope, id, Actor(), body), ct);
    }
}

public sealed class SimulationDisabledException() : InvalidOperationException("Simulation is disabled.");
