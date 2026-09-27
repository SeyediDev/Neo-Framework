using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed partial class WorkItem
{
    public void ReturnStoppedAssignment(WorkspaceScope scope, WorkActor currentOwner, WorkActor recipient, DateTimeOffset now)
    {
        RequireEditable(scope, currentOwner, now); RequireOwner(currentOwner);
        ArgumentNullException.ThrowIfNull(recipient);
        if (Status is not (WorkItemStatus.Review or WorkItemStatus.Blocked or WorkItemStatus.Done) || IsTracking)
            throw new InvalidOperationException("Only a stopped assignment can be returned.");
        OwnerAgentId = recipient.AgentId; OwnerChatId = recipient.ChatId;
        Record(recipient, "AssignmentReturned", "Stopped managed execution returned to its requestor.", now);
    }

    public void Claim(WorkspaceScope scope, RoleProfile role, WorkActor actor, string? branch, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        ArgumentNullException.ThrowIfNull(role);
        role.RequireScope(scope);
        if (!role.IsEnabled) throw new InvalidOperationException("Role is disabled.");
        var validBranch = WorkRules.Optional(branch, 250);
        if (Status == WorkItemStatus.InProgress)
        {
            RequireOwner(actor);
            if (OwnerRoleId != role.Id || Branch != validBranch)
                throw new InvalidOperationException("An active assignment cannot be replaced.");
            return; // Resume is idempotent: it must not restart a manually paused timer.
        }
        if (Status != WorkItemStatus.Ready) throw new InvalidOperationException("Only a Ready item may be claimed.");
        OwnerRoleId = ProjectRules.Id(role.Id);
        OwnerAgentId = actor.AgentId;
        OwnerChatId = actor.ChatId;
        Branch = validBranch;
        Status = WorkItemStatus.InProgress;
        StartTimerInternal(now);
        Record(actor, "Claimed", $"Claimed by role {role.Key}.", now);
    }

    public void ChangeStatus(WorkspaceScope scope, WorkActor actor, WorkItemStatus next, DateTimeOffset now, string? note = null)
    {
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        if (!Enum.IsDefined(next)) throw new ArgumentOutOfRangeException(nameof(next));
        var validNote = WorkRules.Optional(note, 8000);
        if (Status == next) return;
        var allowed = Status switch
        {
            WorkItemStatus.Backlog => next is WorkItemStatus.Ready or WorkItemStatus.Cancelled,
            WorkItemStatus.Ready => next is WorkItemStatus.Blocked or WorkItemStatus.Cancelled,
            WorkItemStatus.InProgress => next is WorkItemStatus.Review or WorkItemStatus.Blocked or WorkItemStatus.Cancelled,
            WorkItemStatus.Blocked => next is WorkItemStatus.Ready or WorkItemStatus.Cancelled,
            WorkItemStatus.Review => next is WorkItemStatus.Ready or WorkItemStatus.Done or WorkItemStatus.Blocked or WorkItemStatus.Cancelled,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Transition {Status} -> {next} is not allowed.");
        var previous = Status;
        StopTimerInternal(now);
        Status = next;
        CompletedAtUtc = next == WorkItemStatus.Done ? now.ToUniversalTime() : null;
        if (next == WorkItemStatus.Ready)
        {
            OwnerRoleId = null; OwnerAgentId = null; OwnerChatId = null; Branch = null;
        }
        Record(actor, "StatusChanged", $"{previous} -> {next}" + (validNote is null ? "" : ": " + validNote), now);
    }

    public void Archive(WorkspaceScope scope, WorkActor actor, DateTimeOffset now)
    {
        RequireScope(scope);
        ArgumentNullException.ThrowIfNull(actor);
        if (IsArchived) return;
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        if (Status is not (WorkItemStatus.Blocked or WorkItemStatus.Done))
            throw new InvalidOperationException("Only Blocked or Done items may be archived.");
        StopTimerInternal(now);
        IsArchived = true;
        ArchivedAtUtc = now.ToUniversalTime();
        Record(actor, "Archived", "Work item archived.", now);
    }

    public void Restore(WorkspaceScope scope, WorkActor actor, DateTimeOffset now)
    {
        RequireScope(scope);
        ArgumentNullException.ThrowIfNull(actor);
        if (OwnerAgentId is not null) RequireOwner(actor);
        if (now < UpdatedAtUtc) throw new InvalidOperationException("Time cannot precede the previous change.");
        if (!IsArchived) return;
        IsArchived = false;
        ArchivedAtUtc = null;
        Record(actor, "Restored", "Work item restored.", now);
    }

    public void AddLog(WorkspaceScope scope, WorkActor actor, string message, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        Record(actor, "Note", WorkRules.Required(message, 8000), now);
    }

    public void SetEstimate(WorkspaceScope scope, WorkActor actor, long? seconds, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        ValidateEstimate(seconds);
        EstimatedSeconds = seconds;
        Record(actor, "EstimateChanged", seconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "No estimate", now);
    }
}
