using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

internal static class WorkItemProjection
{
    public static WorkItemView View(WorkItem item, DateTimeOffset now, IEnumerable<Neo.AgentOrchestration.Domain.Runs.TokenUsageReport>? usage = null) => new(
        item.Id, item.ProjectId, item.ParentWorkItemId, item.Key, item.Title, item.Domain,
        item.Description, item.Status.ToString(), item.Priority.ToString(), item.OwnerRoleId,
        item.OwnerAgentId, item.OwnerChatId, item.Branch, item.IsArchived, item.GetElapsedSeconds(now),
        item.EstimatedSeconds, item.GetBudgetUsedPercent(now), item.IsTracking, item.Version, item.UpdatedAtUtc,
        item.Type.ToString(), item.AcceptanceCriteria,
        item.OwnerHistory.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => new WorkItemOwnerHistoryView(x.Id, x.SourceWorkItemId, x.OriginalStatus,
                x.OwnerRole, x.OwnerAgent, x.OwnerChat, x.CreatedAtUtc)).FirstOrDefault(), item.EstimatedTokens,
        TokenMetering.Summarize(TokenMetering.Reports(item, usage ?? [])), now);

    public static WorkItemDetails Details(WorkItem item, IReadOnlyList<WorkItem> projectItems, DateTimeOffset now,
        IReadOnlyList<Neo.AgentOrchestration.Domain.Runs.TokenUsageReport>? usage = null)
        => new(View(item, now, usage),
            projectItems.Where(x => x.ParentWorkItemId == item.Id).Select(x => View(x, now, usage)).ToArray(),
            item.Dependencies.Select(x => x.DependsOnWorkItemId).ToArray(),
            item.Logs.Select(x => new WorkLogView(x.Id, x.AgentId, x.ChatId, x.Kind, x.Message, x.CreatedAtUtc)).ToArray(),
            item.Evidence.Select(x => new WorkEvidenceView(x.Id, x.Kind.ToString(), x.Reference,
                x.Outcome.ToString(), x.Details, x.CreatedAtUtc, x.Sequence, x.CommitSha)).ToArray(),
            item.TimeEntries.Select(x => new WorkTimeView(x.Id, x.StartedAtUtc, x.EndedAtUtc,
                x.EndedAtUtc.HasValue ? x.DurationSeconds : Math.Max(0L, (long)(now - x.StartedAtUtc).TotalSeconds))).ToArray(),
            item.OwnerHistory.Select(x => new WorkItemOwnerHistoryView(x.Id, x.SourceWorkItemId, x.OriginalStatus,
                x.OwnerRole, x.OwnerAgent, x.OwnerChat, x.CreatedAtUtc)).ToArray(), TokenMetering.Reports(item, usage ?? []));

    public static async Task<WorkItemDetails> ReadDetails(IWorkItemSession session, WorkItem item,
        IReadOnlyList<WorkItem> projectItems, DateTimeOffset now, CancellationToken ct)
    {
        var ids = projectItems.Where(x => x.ParentWorkItemId == item.Id).Select(x => x.Id).Append(item.Id).Distinct().ToArray();
        return Details(item, projectItems, now, await session.GetItemRunUsageAsync(ids, ct));
    }
}
