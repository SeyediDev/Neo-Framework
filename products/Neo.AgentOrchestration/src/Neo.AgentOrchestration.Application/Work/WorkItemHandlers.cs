using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

// Commands are not exposed until a transactional store and membership policies
// are configured. The store owns commit/rollback; domain objects own invariants.
public sealed class WorkItemHandlers(IWorkspaceWorkStore store, TimeProvider clock) :
    IRequestHandler<CreateWorkItem, WorkItemDetails>, IRequestHandler<ClaimWorkItem, WorkItemDetails>,
    IRequestHandler<UpdateWorkItem, WorkItemDetails>, IRequestHandler<GetWorkItem, WorkItemDetails>
{
    public Task<WorkItemDetails> Handle(CreateWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            var project = await session.GetProjectAsync(request.ProjectId, token)
                ?? throw new KeyNotFoundException("Project not found.");
            project.RequireScope(request.Scope);
            WorkItem? parent = request.ParentWorkItemId.HasValue
                ? await Item(session, request.Scope, request.ParentWorkItemId.Value, token) : null;
            var now = clock.GetUtcNow();
            var item = WorkItem.Create(request.Scope, project, request.Key, request.Title, request.Domain,
                request.Actor, now, request.Description, request.Priority, parent, request.EstimatedSeconds);
            var items = await ProjectItems(session, request.Scope, project.Id, token);
            if (items.Any(x => string.Equals(x.Key, item.Key, StringComparison.OrdinalIgnoreCase)))
                throw new WorkItemConflictException("Work item key already exists in this project.");
            session.Add(item);
            return WorkItemProjection.Details(item, items, now);
        }, ct);

    public Task<WorkItemDetails> Handle(ClaimWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            var item = await Item(session, request.Scope, request.WorkItemId, token);
            RequireVersion(item, request.ExpectedVersion);
            var project = await session.GetProjectAsync(item.ProjectId, token)
                ?? throw new KeyNotFoundException("Project not found.");
            project.RequireScope(request.Scope);
            if (!project.IsEnabled) throw new InvalidOperationException("Project is disabled.");
            var role = await session.GetRoleAsync(request.RoleId, token) ?? throw new KeyNotFoundException("Role not found.");
            role.RequireScope(request.Scope);
            if (await session.IsRoleBusyAsync(role.Id, item.Id, token))
                throw new WorkItemConflictException("Role already has an active work item.");
            var items = await ProjectItems(session, request.Scope, item.ProjectId, token);
            item.RequireCompletedDependencies(items);
            var now = clock.GetUtcNow();
            item.Claim(request.Scope, role, request.Actor, request.Branch, now);
            return WorkItemProjection.Details(item, items, now);
        }, ct);

    public Task<WorkItemDetails> Handle(GetWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            var item = await Item(session, request.Scope, request.WorkItemId, token);
            var items = await ProjectItems(session, request.Scope, item.ProjectId, token);
            return WorkItemProjection.Details(item, items, clock.GetUtcNow());
        }, ct);

    public Task<WorkItemDetails> Handle(UpdateWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            var item = await Item(session, request.Scope, request.WorkItemId, token);
            RequireVersion(item, request.ExpectedVersion);
            var items = await ProjectItems(session, request.Scope, item.ProjectId, token);
            var now = clock.GetUtcNow();
            Apply(request, item, items, now);
            return WorkItemProjection.Details(item, items, now);
        }, ct);

    private static void Apply(UpdateWorkItem request, WorkItem item, IReadOnlyList<WorkItem> items, DateTimeOffset now)
    {
        switch (request.Change)
        {
            case StatusChange change:
                if (change.Status == WorkItemStatus.Done)
                {
                    item.RequireCompletedDependencies(items);
                    if (items.Any(x => x.ParentWorkItemId == item.Id &&
                        x.Status is not (WorkItemStatus.Done or WorkItemStatus.Cancelled)))
                        throw new WorkItemConflictException("Complete or explicitly cancel child items first.");
                }
                item.ChangeStatus(request.Scope, request.Actor, change.Status, now, change.Note); break;
            case LogChange change: item.AddLog(request.Scope, request.Actor, change.Message, now); break;
            case EstimateChange change: item.SetEstimate(request.Scope, request.Actor, change.Seconds, now); break;
            case TrackingChange change:
                if (change.Start) item.StartTimer(request.Scope, request.Actor, now);
                else item.StopTimer(request.Scope, request.Actor, now);
                break;
            case ArchiveChange change:
                if (change.Archive) item.Archive(request.Scope, request.Actor, now);
                else item.Restore(request.Scope, request.Actor, now);
                break;
            case EvidenceChange change: item.AddEvidence(request.Scope, request.Actor, change.Kind, change.Reference,
                change.Outcome, change.Details, now); break;
            case DependencyChange change:
                var dependency = items.SingleOrDefault(x => x.Id == change.DependsOnWorkItemId)
                    ?? throw new KeyNotFoundException("Dependency not found in project.");
                item.AddDependency(request.Scope, request.Actor, dependency, items, now); break;
            default: throw new ArgumentException("Unknown work item change.");
        }
    }

    private static async Task<WorkItem> Item(IWorkItemSession session, WorkspaceScope scope, Guid id, CancellationToken ct)
    {
        var item = await session.GetItemAsync(id, ct) ?? throw new KeyNotFoundException("Work item not found.");
        item.RequireScope(scope);
        return item;
    }

    private static async Task<IReadOnlyList<WorkItem>> ProjectItems(IWorkItemSession session,
        WorkspaceScope scope, Guid projectId, CancellationToken ct)
    {
        var items = await session.GetProjectItemsAsync(projectId, ct);
        foreach (var item in items)
        {
            item.RequireScope(scope);
            if (item.ProjectId != projectId) throw new InvalidOperationException("Store returned a foreign project.");
        }
        return items;
    }

    private static void RequireVersion(WorkItem item, Guid expected)
    {
        if (expected == Guid.Empty || item.Version != expected)
            throw new WorkItemConflictException("Work item changed; reload before applying this operation.");
    }
}
