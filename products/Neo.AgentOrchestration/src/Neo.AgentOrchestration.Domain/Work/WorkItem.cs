using Neo.AgentOrchestration.Domain.Projects;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed partial class WorkItem : BaseEntity<Guid>
{
    private readonly List<WorkItemLog> _logs = [];
    private readonly List<WorkItemEvidence> _evidence = [];
    private readonly List<WorkItemTimeEntry> _timeEntries = [];
    private readonly List<WorkItemDependency> _dependencies = [];

    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? ParentWorkItemId { get; private set; }
    public string Key { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Domain { get; private set; } = "";
    public string? Description { get; private set; }
    public WorkItemStatus Status { get; private set; } = WorkItemStatus.Backlog;
    public WorkItemPriority Priority { get; private set; }
    public Guid? OwnerRoleId { get; private set; }
    public string? OwnerAgentId { get; private set; }
    public string? OwnerChatId { get; private set; }
    public string? Branch { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public bool IsArchived { get; private set; }
    public long? EstimatedSeconds { get; private set; }
    public Guid Version { get; private set; }
    public IReadOnlyList<WorkItemLog> Logs => _logs.AsReadOnly();
    public IReadOnlyList<WorkItemEvidence> Evidence => _evidence.AsReadOnly();
    public IReadOnlyList<WorkItemTimeEntry> TimeEntries => _timeEntries.AsReadOnly();
    public IReadOnlyList<WorkItemDependency> Dependencies => _dependencies.AsReadOnly();

    public WorkItem() { }

    public static WorkItem Create(WorkspaceScope scope, Project project, string key, string title,
        string domain, WorkActor actor, DateTimeOffset now, string? description = null,
        WorkItemPriority priority = WorkItemPriority.Normal, WorkItem? parent = null, long? estimatedSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(actor);
        project.RequireScope(scope);
        if (!project.IsEnabled) throw new InvalidOperationException("Project is disabled.");
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        ValidateEstimate(estimatedSeconds);
        if (parent is not null)
        {
            parent.RequireScope(scope);
            if (parent.ProjectId != project.Id || parent.IsArchived || parent.IsClosed)
                throw new InvalidOperationException("Parent must be an open item in the same project.");
        }
        var item = new WorkItem
        {
            Id = Guid.NewGuid(), OrganizationId = project.OrganizationId, WorkspaceId = project.WorkspaceId,
            ProjectId = ProjectRules.Id(project.Id), ParentWorkItemId = parent?.Id,
            Key = ProjectRules.Key(key), Title = WorkRules.Required(title, 200),
            Domain = WorkRules.Required(domain, 80), Description = WorkRules.Optional(description, 32000),
            Priority = priority, EstimatedSeconds = estimatedSeconds, CreatedAtUtc = now.ToUniversalTime()
        };
        item.Record(actor, "Created", "Work item created.", now);
        return item;
    }

    public void RequireScope(WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Require(OrganizationId, WorkspaceId);
    }

    private bool IsClosed => Status is WorkItemStatus.Done or WorkItemStatus.Cancelled;
    private bool IsOwner(WorkActor actor) => OwnerAgentId == actor.AgentId && OwnerChatId == actor.ChatId;

    private void RequireEditable(WorkspaceScope scope, WorkActor actor, DateTimeOffset now)
    {
        RequireScope(scope);
        ArgumentNullException.ThrowIfNull(actor);
        if (IsArchived) throw new InvalidOperationException("Restore the archived item first.");
        if (now < UpdatedAtUtc) throw new InvalidOperationException("Time cannot precede the previous change.");
    }

    private void RequireOwner(WorkActor actor)
    {
        if (!IsOwner(actor)) throw new InvalidOperationException("Another agent or chat owns this item.");
    }

    private void Record(WorkActor actor, string kind, string message, DateTimeOffset now)
    {
        UpdatedAtUtc = now.ToUniversalTime();
        Version = Guid.NewGuid();
        _logs.Add(WorkItemLog.Create(Id, actor, kind, message, UpdatedAtUtc));
    }

    private static void ValidateEstimate(long? seconds)
    {
        if (seconds is <= 0) throw new ArgumentOutOfRangeException(nameof(seconds), "Estimate must be positive or absent.");
    }
}
