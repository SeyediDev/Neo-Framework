using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed partial class WorkItem
{
    public void AddEvidence(WorkspaceScope scope, WorkActor actor, EvidenceKind kind, string reference,
        EvidenceOutcome outcome, string? details, DateTimeOffset now, string? commitSha = null)
    {
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        var evidence = WorkItemEvidence.Create(Id, kind, reference, outcome, details, now,
            checked(_evidence.Select(x => x.Sequence).DefaultIfEmpty().Max() + 1), commitSha);
        if (evidence.CommitSha is not null && !_evidence.Any(x => x.Kind == EvidenceKind.Commit &&
            string.Equals(x.Reference, evidence.CommitSha, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Record the referenced commit on this item first.");
        _evidence.Add(evidence);
        Record(actor, "EvidenceAdded", $"{kind}: {evidence.Reference} ({outcome})", now);
    }

    public void AddDependency(WorkspaceScope scope, WorkActor actor, WorkItem dependency,
        IReadOnlyCollection<WorkItem> projectGraph, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        ArgumentNullException.ThrowIfNull(dependency);
        dependency.RequireScope(scope);
        if (OwnerAgentId is not null) RequireOwner(actor);
        if (Status is not (WorkItemStatus.Backlog or WorkItemStatus.Ready or WorkItemStatus.Blocked))
            throw new InvalidOperationException("Dependencies may change only while planning or blocked.");
        if (dependency.ProjectId != ProjectId || dependency.Id == Id)
            throw new InvalidOperationException("Dependency must be another item in the same project.");
        if (_dependencies.Any(x => x.DependsOnWorkItemId == dependency.Id)) return;
        DependencyGraph.EnsureAcyclicAddition(this, dependency, projectGraph);
        _dependencies.Add(WorkItemDependency.Create(ProjectId, Id, dependency.Id, now));
        Record(actor, "DependencyAdded", dependency.Key, now);
    }

    public void RequireCompletedDependencies(IReadOnlyCollection<WorkItem> projectItems)
    {
        foreach (var edge in _dependencies)
        {
            var dependency = projectItems.SingleOrDefault(x => x.Id == edge.DependsOnWorkItemId &&
                x.OrganizationId == OrganizationId && x.WorkspaceId == WorkspaceId && x.ProjectId == ProjectId);
            if (dependency?.Status != WorkItemStatus.Done)
                throw new InvalidOperationException("A dependency is missing or unfinished.");
        }
    }
}
