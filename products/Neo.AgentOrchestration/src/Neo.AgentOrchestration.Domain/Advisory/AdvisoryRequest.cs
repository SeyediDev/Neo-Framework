using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Advisory;

public enum AdvisoryState : byte { Queued = 1, Submitting = 2, Draft = 3, Held = 4, Cancelled = 5, Expired = 6, Rejected = 7 }
public sealed record AdvisoryDraft(string Text, string? Model, long? InputTokens, long? OutputTokens, string? NativeRunId = null);

// A private suggestion, never a coding AgentRun, work assignment or approval.
public sealed class AdvisoryRequest : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid WorkItemVersion { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid DeliveryOperationId { get; private set; }
    public Guid Version { get; private set; }
    public string RequesterId { get; private set; } = "";
    public string RequesterChatId { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public string Brief { get; private set; } = "";
    public AdvisoryState State { get; private set; }
    public bool CancelRequested { get; private set; }
    public string? Reason { get; private set; }
    public string? Draft { get; private set; }
    public string? Model { get; private set; }
    public string? NativeRunId { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset DeadlineUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public AdvisoryRequest() { }

    public static AdvisoryRequest Create(WorkspaceScope scope, WorkItem item, WorkActor actor,
        Guid requestId, Guid expectedVersion, string brief, bool consent, int budgetSeconds,
        string fingerprint, Guid deliveryId, DateTimeOffset now)
    {
        item.RequireScope(scope);
        if (requestId == Guid.Empty || deliveryId == Guid.Empty || expectedVersion == Guid.Empty || !consent ||
            budgetSeconds is < 30 or > 180) throw new ArgumentException("Invalid advisory request or consent.");
        if (item.Version != expectedVersion || item.IsArchived || item.Status is WorkItemStatus.Cancelled)
            throw new InvalidOperationException("The task snapshot is not eligible.");
        return new() { Id = Guid.NewGuid(), OrganizationId = scope.OrganizationId, WorkspaceId = scope.WorkspaceId,
            ProjectId = item.ProjectId, WorkItemId = item.Id, WorkItemVersion = item.Version,
            RequestId = requestId, DeliveryOperationId = deliveryId, Version = Guid.NewGuid(),
            RequesterId = actor.AgentId, RequesterChatId = actor.ChatId,
            Fingerprint = WorkRules.Required(fingerprint, 64), Brief = WorkRules.Required(brief, 2000),
            State = AdvisoryState.Queued, CreatedAtUtc = now, DeadlineUtc = now.AddSeconds(budgetSeconds) };
    }

    public void RequireRequester(WorkspaceScope scope, WorkActor actor)
    {
        scope.Require(OrganizationId, WorkspaceId);
        if (!string.Equals(RequesterId, actor.AgentId, StringComparison.Ordinal))
            throw new KeyNotFoundException("Private advisory request not found.");
    }

    public bool Begin(DateTimeOffset now, bool snapshotEligible)
    {
        if (State == AdvisoryState.Submitting)
        { Hold("submission-uncertain", now); return false; } // recovery NEVER resends
        if (State != AdvisoryState.Queued) return false;
        if (now >= DeadlineUtc) { Stop(AdvisoryState.Expired, "deadline-before-submission", now); return false; }
        if (!snapshotEligible) { Stop(AdvisoryState.Rejected, "task-snapshot-changed", now); return false; }
        State = AdvisoryState.Submitting; StartedAtUtc = now; Version = Guid.NewGuid(); return true;
    }

    public void Cancel(Guid expectedVersion, DateTimeOffset now)
    {
        if (Version != expectedVersion) throw new InvalidOperationException("Advisory changed.");
        if (State is AdvisoryState.Cancelled or AdvisoryState.Expired or AdvisoryState.Rejected) return;
        CancelRequested = true;
        if (State is AdvisoryState.Queued or AdvisoryState.Draft)
        { Draft = null; Stop(AdvisoryState.Cancelled, "cancelled", now); }
        else Version = Guid.NewGuid(); // request acknowledged, native stop NOT confirmed
    }

    public void Complete(AdvisoryDraft result, DateTimeOffset now)
    {
        if (State is not (AdvisoryState.Submitting or AdvisoryState.Held))
            throw new InvalidOperationException("No advisory submission is pending.");
        if (result.InputTokens is < 0 || result.OutputTokens is < 0)
            throw new ArgumentException("Invalid usage counters.");
        var text = WorkRules.Required(result.Text, 12000);
        Model = WorkRules.Optional(result.Model, 200); InputTokens = result.InputTokens; OutputTokens = result.OutputTokens;
        NativeRunId = WorkRules.Optional(result.NativeRunId, 200);
        // Keep measured consumption even if cancellation/deadline discards the text.
        if (CancelRequested) Stop(AdvisoryState.Cancelled, "completed-after-cancellation", now);
        else if (now >= DeadlineUtc) Stop(AdvisoryState.Expired, "completed-after-deadline", now);
        else { Draft = text; Stop(AdvisoryState.Draft, "draft-needs-human-review", now); }
    }
    public void Hold(string reason, DateTimeOffset now)
    {
        if (State is AdvisoryState.Submitting or AdvisoryState.Held) Stop(AdvisoryState.Held, reason, now);
    }
    public void Reject(string reason, DateTimeOffset now)
    {
        if (State == AdvisoryState.Submitting) Stop(AdvisoryState.Rejected, reason, now);
    }
    public void DeliveryExhausted(DateTimeOffset now)
    {
        if (State == AdvisoryState.Submitting) Hold("delivery-exhausted-reconcile", now);
        else if (State == AdvisoryState.Queued) Stop(AdvisoryState.Rejected, "delivery-not-submitted", now);
    }
    private void Stop(AdvisoryState state, string reason, DateTimeOffset now)
    { State = state; Reason = WorkRules.Required(reason, 80); FinishedAtUtc = now; Version = Guid.NewGuid(); }
}
