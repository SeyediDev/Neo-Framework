using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed partial class WorkItem
{
    public bool IsTracking => _timeEntries.Any(x => x.EndedAtUtc is null);

    public long GetElapsedSeconds(DateTimeOffset now)
        => _timeEntries.Sum(x => x.EndedAtUtc.HasValue ? x.DurationSeconds :
            Math.Max(0L, (long)(now - x.StartedAtUtc).TotalSeconds));

    // Time-budget consumption is not a claim about how much of the work is done.
    public decimal? GetBudgetUsedPercent(DateTimeOffset now)
        => EstimatedSeconds is > 0 ? decimal.Round(100m * GetElapsedSeconds(now) / EstimatedSeconds.Value, 1) : null;

    public void StartTimer(WorkspaceScope scope, WorkActor actor, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        RequireOwner(actor);
        if (Status != WorkItemStatus.InProgress) throw new InvalidOperationException("Timer requires InProgress.");
        if (IsTracking) return;
        StartTimerInternal(now);
        Record(actor, "TimerStarted", "Active interval started.", now);
    }

    public void StopTimer(WorkspaceScope scope, WorkActor actor, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        RequireOwner(actor);
        if (!IsTracking) return;
        StopTimerInternal(now);
        Record(actor, "TimerStopped", "Active interval stopped.", now);
    }

    private void StartTimerInternal(DateTimeOffset now)
    {
        if (!IsTracking) _timeEntries.Add(WorkItemTimeEntry.Start(Id, now));
    }

    private void StopTimerInternal(DateTimeOffset now)
        => _timeEntries.SingleOrDefault(x => x.EndedAtUtc is null)?.Stop(now);
}
