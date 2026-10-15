using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Advisory;

public interface IAdvisoryRequests
{
    Task<AdvisoryRequestView> CreateAsync(WorkspaceScope scope, Guid itemId, WorkActor actor, CreateAdvisoryRequest body, CancellationToken ct);
    Task<AdvisoryRequestCatalog> ListAsync(WorkspaceScope scope, Guid itemId, WorkActor actor, CancellationToken ct);
    Task<AdvisoryRequestView> GetAsync(WorkspaceScope scope, Guid id, WorkActor actor, CancellationToken ct);
    Task<AdvisoryRequestView> CancelAsync(WorkspaceScope scope, Guid id, WorkActor actor, Guid version, CancellationToken ct);
}
public sealed record StartAdvisory(WorkspaceScope Scope, Guid ItemId, WorkActor Actor, CreateAdvisoryRequest Body) : IRequest<AdvisoryRequestView>;
public sealed record ListAdvisories(WorkspaceScope Scope, Guid ItemId, WorkActor Actor) : IRequest<AdvisoryRequestCatalog>;
public sealed record GetAdvisory(WorkspaceScope Scope, Guid Id, WorkActor Actor) : IRequest<AdvisoryRequestView>;
public sealed record CancelAdvisory(WorkspaceScope Scope, Guid Id, WorkActor Actor, Guid Version) : IRequest<AdvisoryRequestView>;
public sealed class AdvisoryHandlers(IAdvisoryRequests store) :
    IRequestHandler<StartAdvisory, AdvisoryRequestView>, IRequestHandler<ListAdvisories, AdvisoryRequestCatalog>,
    IRequestHandler<GetAdvisory, AdvisoryRequestView>, IRequestHandler<CancelAdvisory, AdvisoryRequestView>
{
    public Task<AdvisoryRequestView> Handle(StartAdvisory r, CancellationToken ct) => store.CreateAsync(r.Scope, r.ItemId, r.Actor, r.Body, ct);
    public Task<AdvisoryRequestCatalog> Handle(ListAdvisories r, CancellationToken ct) => store.ListAsync(r.Scope, r.ItemId, r.Actor, ct);
    public Task<AdvisoryRequestView> Handle(GetAdvisory r, CancellationToken ct) => store.GetAsync(r.Scope, r.Id, r.Actor, ct);
    public Task<AdvisoryRequestView> Handle(CancelAdvisory r, CancellationToken ct) => store.CancelAsync(r.Scope, r.Id, r.Actor, r.Version, ct);
}
