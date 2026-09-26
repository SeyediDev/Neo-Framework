using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

internal static class WorkItemProjection
{
    public static WorkItemView View(WorkItem item, DateTimeOffset now) => new(
        item.Id, item.ProjectId, item.ParentWorkItemId, item.Key, item.Title, item.Domain,
        item.Description, item.Status.ToString(), item.Priority.ToString(), item.OwnerRoleId,
        item.OwnerAgentId, item.OwnerChatId, item.Branch, item.IsArchived, item.GetElapsedSeconds(now),
        item.EstimatedSeconds, item.GetBudgetUsedPercent(now), item.IsTracking, item.Version, item.UpdatedAtUtc);

    public static WorkItemDetails Details(WorkItem item, IReadOnlyList<WorkItem> projectItems, DateTimeOffset now)
        => new(View(item, now),
            projectItems.Where(x => x.ParentWorkItemId == item.Id).Select(x => View(x, now)).ToArray(),
            item.Dependencies.Select(x => x.DependsOnWorkItemId).ToArray(),
            item.Logs.Select(x => new WorkLogView(x.Id, x.AgentId, x.ChatId, x.Kind, x.Message, x.CreatedAtUtc)).ToArray(),
            item.Evidence.Select(x => new WorkEvidenceView(x.Id, x.Kind.ToString(), x.Reference,
                x.Outcome.ToString(), x.Details, x.CreatedAtUtc, x.Sequence, x.CommitSha)).ToArray(),
            item.TimeEntries.Select(x => new WorkTimeView(x.Id, x.StartedAtUtc, x.EndedAtUtc,
                x.EndedAtUtc.HasValue ? x.DurationSeconds : Math.Max(0L, (long)(now - x.StartedAtUtc).TotalSeconds))).ToArray());
}
