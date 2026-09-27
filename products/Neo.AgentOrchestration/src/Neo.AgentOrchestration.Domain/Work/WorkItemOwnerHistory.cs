using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Work;

/// <summary>Immutable ownership captured during migration or reassignment.</summary>
public sealed class WorkItemOwnerHistory : BaseEntity<Guid>
{
    public Guid ProjectId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public long SourceWorkItemId { get; private set; }
    public int OriginalStatus { get; private set; }
    public string? OwnerRole { get; private set; }
    public string? OwnerAgent { get; private set; }
    public string? OwnerChat { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private WorkItemOwnerHistory() { }
}
