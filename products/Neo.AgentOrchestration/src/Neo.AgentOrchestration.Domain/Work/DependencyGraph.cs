namespace Neo.AgentOrchestration.Domain.Work;

internal static class DependencyGraph
{
    public static void EnsureAcyclicAddition(WorkItem source, WorkItem target, IReadOnlyCollection<WorkItem> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Any(x => x.ProjectId != source.ProjectId || x.WorkspaceId != source.WorkspaceId ||
                           x.OrganizationId != source.OrganizationId))
            throw new InvalidOperationException("The dependency graph contains another project.");
        var nodes = graph.ToDictionary(x => x.Id);
        if (!nodes.ContainsKey(source.Id) || !nodes.ContainsKey(target.Id))
            throw new InvalidOperationException("The dependency graph is incomplete.");
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(target.Id);
        while (pending.TryPop(out var id))
        {
            if (id == source.Id) throw new InvalidOperationException("Dependency would create a cycle.");
            if (!visited.Add(id)) continue;
            if (!nodes.TryGetValue(id, out var node)) throw new InvalidOperationException("The dependency graph is incomplete.");
            foreach (var dependency in node.Dependencies) pending.Push(dependency.DependsOnWorkItemId);
        }
    }
}
