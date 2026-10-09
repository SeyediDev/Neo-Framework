namespace Neo.AgentOrchestration.Contracts;

public sealed record ProjectDeletionPreview(Guid ProjectId, string Key, string Name, string Snapshot,
    IReadOnlyDictionary<string, int> Records, IReadOnlyDictionary<string, int> WorkItemTypes,
    int Subtasks, int ActiveManualAssignments, int ActiveRuns, int PendingDeliveries)
{
    public bool CanDelete => ActiveRuns == 0 && PendingDeliveries == 0;
}

public sealed record DeleteProjectRequest(string ExpectedSnapshot, string ConfirmProjectKey,
    bool IncludeActiveManualAssignments = false);

public sealed record ProjectDeletionResult(Guid ProjectId, string Key,
    IReadOnlyDictionary<string, int> DeletedRecords, DateTimeOffset DeletedAtUtc);
