namespace Neo.AgentOrchestration.Contracts;

public sealed record WorkContextLink(Guid Id, string Key, string Status, Guid Version);
public sealed record WorkContextView(WorkItemView Item, IReadOnlyList<WorkContextLink> Children,
    IReadOnlyList<Guid> Dependencies, IReadOnlyList<WorkLogView> RecentLogs,
    IReadOnlyList<WorkEvidenceView> RecentEvidence, int TotalLogs, int TotalEvidence,
    int TotalChildren, int TotalDependencies, bool Unchanged, IReadOnlyList<string> Omitted);
public sealed record WorkHistoryPage(Guid Version, IReadOnlyList<WorkLogView> Logs,
    int Total, int? NextSkip);
public sealed record WorkMutationReceipt(WorkItemView Item, int LogCount, int EvidenceCount,
    string DetailResource, string Note);

// Lossless storage remains authoritative. Bounded projections explicitly report omissions.
public static class WorkContextProjection
{
    public static WorkContextView Create(WorkItemDetails details, Guid? knownVersion = null)
    {
        var omitted = new List<string>();
        var unchanged = knownVersion == details.Item.Version;
        string? Clip(string? text, string field, int max = 2000)
        {
            if (text is null || text.Length <= max) return text;
            omitted.Add(field);
            var end = max;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            return text[..end];
        }
        var item = details.Item with {
            Description = unchanged ? null : Clip(details.Item.Description, "item.description"),
            AcceptanceCriteria = unchanged ? null : Clip(details.Item.AcceptanceCriteria, "item.acceptanceCriteria"),
            LastOwnerHistory = null
        };
        var logs = unchanged ? [] : details.Logs.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).TakeLast(3)
            .Select(x => x with { Message = Clip(x.Message, $"logs/{x.Id}", 1000)! }).ToArray();
        var evidence = unchanged ? [] : details.Evidence.OrderBy(x => x.Sequence).TakeLast(3)
            .Select(x => x with { Details = Clip(x.Details, $"evidence/{x.Id}/details", 1000),
                Reference = Clip(x.Reference, $"evidence/{x.Id}/reference", 500)! }).ToArray();
        var children = details.Children.OrderBy(x => x.Id).Take(50)
            .Select(x => new WorkContextLink(x.Id, x.Key, x.Status, x.Version)).ToArray();
        var dependencies = details.Dependencies.Order().Take(50).ToArray();
        if (details.Logs.Count > logs.Length) omitted.Add("olderLogs");
        if (details.Evidence.Count > evidence.Length) omitted.Add("olderEvidence");
        if (details.Children.Count > children.Length) omitted.Add("additionalChildren");
        if (details.Dependencies.Count > dependencies.Length) omitted.Add("additionalDependencies");
        if (details.TimeEntries.Count > 0) omitted.Add("timeEntries");
        if (details.OwnerHistory.Count > 0) omitted.Add("ownerHistory");
        if (details.TokenUsage?.Count > 0) omitted.Add("tokenUsageReports");
        if (unchanged) omitted.Add("unchangedItemTextAndHistory");
        return new(item, children, dependencies, logs, evidence, details.Logs.Count, details.Evidence.Count,
            details.Children.Count, details.Dependencies.Count, unchanged, omitted);
    }

    public static WorkHistoryPage History(WorkItemDetails details, int skip, int take)
    {
        if (skip < 0 || take is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(take));
        var logs = details.Logs.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(take).ToArray();
        return new(details.Item.Version, logs, details.Logs.Count,
            (long)skip + logs.Length < details.Logs.Count ? skip + logs.Length : null);
    }

    public static WorkMutationReceipt Receipt(WorkItemDetails details) => new(
        details.Item with { Description = null, AcceptanceCriteria = null, LastOwnerHistory = null },
        details.Logs.Count, details.Evidence.Count, $"items/{details.Item.Id:D}",
        "Mutation acknowledged; original text/history retained. Use context or details only when needed.");
}
