namespace Neo.AgentOrchestration.Contracts;

public sealed record WorkItemView(Guid Id, Guid ProjectId, Guid? ParentWorkItemId, string Key,
    string Title, string Domain, string? Description, string Status, string Priority,
    Guid? OwnerRoleId, string? OwnerAgentId, string? OwnerChatId, string? Branch,
    bool IsArchived, long ElapsedSeconds, long? EstimatedSeconds, decimal? BudgetUsedPercent,
    bool IsTracking, Guid Version, DateTimeOffset UpdatedAtUtc, string Type = "Task", string? AcceptanceCriteria = null);
public sealed record WorkLogView(Guid Id, string AgentId, string ChatId, string Kind, string Message, DateTimeOffset CreatedAtUtc);
public sealed record WorkEvidenceView(Guid Id, string Kind, string Reference, string Outcome,
    string? Details, DateTimeOffset CreatedAtUtc, int Sequence, string? CommitSha);
public sealed record WorkTimeView(Guid Id, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, long DurationSeconds);
public sealed record WorkItemOwnerHistoryView(Guid Id, long SourceWorkItemId, int OriginalStatus,
    string? OwnerRole, string? OwnerAgent, string? OwnerChat, DateTimeOffset CreatedAtUtc);
public sealed record WorkItemDetails(WorkItemView Item, IReadOnlyList<WorkItemView> Children,
    IReadOnlyList<Guid> Dependencies, IReadOnlyList<WorkLogView> Logs,
    IReadOnlyList<WorkEvidenceView> Evidence, IReadOnlyList<WorkTimeView> TimeEntries,
    IReadOnlyList<WorkItemOwnerHistoryView> OwnerHistory);
