using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Application.ExternalExecution;

public interface IAgentRuntimeStatus
{
    Task<AgentRuntimeCatalog> ReadAsync(WorkspaceScope scope, Guid projectId, CancellationToken ct);
}
public sealed record GetAgentRuntimeStatus(WorkspaceScope Scope, Guid ProjectId) : IRequest<AgentRuntimeCatalog>;
public sealed class GetAgentRuntimeStatusHandler(IAgentRuntimeStatus status)
    : IRequestHandler<GetAgentRuntimeStatus, AgentRuntimeCatalog>
{
    public Task<AgentRuntimeCatalog> Handle(GetAgentRuntimeStatus request, CancellationToken ct)
    {
        if (request.ProjectId == Guid.Empty) throw new ArgumentException("Project is required.");
        return status.ReadAsync(request.Scope, request.ProjectId, ct);
    }
}
