using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Workspace;

public interface IProjectDeletionStore
{
    Task<ProjectDeletionPreview> PreviewAsync(WorkspaceScope scope, Guid projectId, CancellationToken ct);
    Task<ProjectDeletionResult> DeleteAsync(WorkspaceScope scope, Guid projectId, DeleteProjectRequest request, CancellationToken ct);
}

public sealed record PreviewProjectDeletion(WorkspaceScope Scope, Guid ProjectId) : IRequest<ProjectDeletionPreview>;
public sealed record DeleteProject(WorkspaceScope Scope, Guid ProjectId, WorkActor Actor, DeleteProjectRequest Value)
    : IRequest<ProjectDeletionResult>;

public sealed class ProjectDeletionHandlers(IProjectDeletionStore store) :
    IRequestHandler<PreviewProjectDeletion, ProjectDeletionPreview>, IRequestHandler<DeleteProject, ProjectDeletionResult>
{
    public Task<ProjectDeletionPreview> Handle(PreviewProjectDeletion r, CancellationToken ct)
        => store.PreviewAsync(r.Scope, r.ProjectId, ct);
    public Task<ProjectDeletionResult> Handle(DeleteProject r, CancellationToken ct)
    {
        if (r.Value.ExpectedSnapshot is not { Length: 64 } snapshot || !snapshot.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(r.Value.ConfirmProjectKey))
            throw new ArgumentException("An exact project key and deletion snapshot are required.");
        return store.DeleteAsync(r.Scope, r.ProjectId, r.Value, ct);
    }
}
