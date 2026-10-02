using MediatR;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

// The API authorizes workspace membership before sending commands. The store
// owns commit/rollback; domain objects own invariants and owner/version checks.
public sealed class WorkItemHandlers(IWorkspaceWorkStore store, TimeProvider clock) :
    IRequestHandler<CreateWorkItem, WorkItemDetails>, IRequestHandler<ClaimWorkItem, WorkItemDetails>,
    IRequestHandler<UpdateWorkItem, WorkItemDetails>, IRequestHandler<GetWorkItem, WorkItemDetails>, IRequestHandler<GetWorkHistory, WorkHistoryPage>
{
    public Task<WorkItemDetails> Handle(CreateWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            string? fingerprint = null;
            if (request.RequestId is { } requestId)
            {
                if (requestId == Guid.Empty) throw new ArgumentException("RequestId must be nonempty when supplied.");
                fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    request.ProjectId, request.Key, request.Title, request.Domain, request.Description,
                    request.Priority, request.ParentWorkItemId, request.EstimatedSeconds, request.Type, request.AcceptanceCriteria
                }))));
                var prefix = $"{requestId:D}:";
                // Include all projects and archived records in this workspace.
                // The store's transaction lock serializes concurrent identical intake.
                foreach (var candidateProject in await session.GetProjectsAsync(token))
                {
                    var candidates = await ProjectItems(session, request.Scope, candidateProject.Id, token);
                    foreach (var candidate in candidates)
                    {
                        var receipt = candidate.Logs.SingleOrDefault(x => x.Kind == "ChatIntake" && x.Message.StartsWith(prefix, StringComparison.Ordinal));
                        if (receipt is null) continue;
                        if (receipt.Message != prefix + fingerprint || receipt.AgentId != request.Actor.AgentId || receipt.ChatId != request.Actor.ChatId)
                            throw new WorkItemConflictException("RequestId was already used with different content or source identity.");
                        return WorkItemProjection.Details(candidate, candidates, clock.GetUtcNow());
                    }
                }
            }
            var project = await session.GetProjectAsync(request.ProjectId, token)
                ?? throw new KeyNotFoundException("Project not found.");
            project.RequireScope(request.Scope);
            WorkItem? parent = request.ParentWorkItemId.HasValue
                ? await Item(session, request.Scope, request.ParentWorkItemId.Value, token) : null;
            var now = clock.GetUtcNow();
            var item = WorkItem.Create(request.Scope, project, request.Key, request.Title, request.Domain,
                request.Actor, now, request.Description, request.Priority, parent, request.EstimatedSeconds,
                request.Type, request.AcceptanceCriteria);
            var items = await ProjectItems(session, request.Scope, project.Id, token);
            if (items.Any(x => string.Equals(x.Key, item.Key, StringComparison.OrdinalIgnoreCase)))
                throw new WorkItemConflictException("Work item key already exists in this project.");
            if (request.RequestId is { } intakeId) item.RecordIntake(request.Scope, request.Actor, intakeId, fingerprint!, now);
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

    public Task<WorkHistoryPage> Handle(GetWorkHistory request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, (session, token) =>
            session.GetHistoryPageAsync(request.WorkItemId, request.Skip, request.Take, request.SnapshotVersion, token), ct);

    public Task<WorkItemDetails> Handle(UpdateWorkItem request, CancellationToken ct)
        => store.ExecuteAsync(request.Scope, async (session, token) =>
        {
            var item = await Item(session, request.Scope, request.WorkItemId, token);
            RequireVersion(item, request.ExpectedVersion);
            var items = await ProjectItems(session, request.Scope, item.ProjectId, token);
            if (await session.GetProjectAsync(item.ProjectId, token) is not { IsEnabled: true })
                throw new InvalidOperationException("Project is disabled.");
            var now = clock.GetUtcNow();
            Apply(request, item, items, now);
            return WorkItemProjection.Details(item, items, now);
        }, ct);

    private static void Apply(UpdateWorkItem request, WorkItem item, IReadOnlyList<WorkItem> items, DateTimeOffset now)
    {
        switch (request.Change)
        {
            case PlanningChange change:
                item.SetPlanning(request.Scope, request.Actor, change.Type, change.AcceptanceCriteria, now); break;
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
                change.Outcome, change.Details, now, change.CommitSha); break;
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
