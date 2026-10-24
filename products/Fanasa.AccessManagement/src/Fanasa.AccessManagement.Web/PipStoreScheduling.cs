namespace Fanasa.AccessManagement.Web.Governance;

public sealed partial class PipStore
{
    private const string LeaseDocumentKind = "pip.lease";
    private const string CheckpointDocumentKind = "pip.checkpoint";

    public PipSourceDefinition[] ScheduledSources()
    {
        PipSourceDefinition[] Read()
        {
            var activeTenants = access.GetTenants().Where(x => x.IsActive).Select(x => x.Id).ToHashSet();
            return database.All<PipSourceDefinition>(SourceDocumentKind)
                .Where(x => activeTenants.Contains(x.TenantId) && x.Enabled && !x.Educational
                    && x.PollIntervalSeconds is >= 30 and <= 86400 && x.SyncSubjects is { Length: > 0 })
                .ToArray();
        }
        return database.IsInTransaction ? Read() : database.ReadSnapshot(Read);
    }

    public PipSyncRun? Checkpoint(Guid tenantId, Guid sourceId, string subject)
    {
        var source = ReadSource(tenantId, sourceId); ValidateSubject(subject);
        var run = database.Read<PipSyncRun>(CheckpointDocumentKind, SnapshotKey(tenantId, subject, sourceId));
        return run is not null && run.TenantId == tenantId && run.SourceId == sourceId && run.Subject == subject
            && (run.SourceGeneration == source.Generation || source.Educational && run.SourceGeneration == 0)
            && run.CompletedAt >= run.StartedAt && run.CompletedAt.Offset == TimeSpan.Zero ? run : null;
    }

    /// <summary>Successful evidence may expire before the poll interval; failures retain the configured retry backoff.</summary>
    public DateTimeOffset? NextDueAt(PipSourceDefinition source, string subject, DateTimeOffset now)
    {
        DateTimeOffset? ReadDue()
        {
            ValidateSubject(subject);
            var current = ReadSource(source.TenantId, source.Id);
            if (!current.Enabled || current.Educational || current.PollIntervalSeconds is < 30 or > 86400
                || !(current.SyncSubjects ?? []).Contains(subject, StringComparer.Ordinal)) return null;
            var last = Checkpoint(current.TenantId, current.Id, subject);
            if (last is null || last.SourceGeneration != current.Generation || last.CompletedAt > now) return now;
            var due = last.CompletedAt.AddSeconds(current.PollIntervalSeconds);
            if (last.Status is "succeeded" or "not-modified")
            {
                var snapshot = this.Read(current.TenantId, subject, now, current.Id).Snapshot;
                if (snapshot is not null && snapshot.ContentHash == last.SnapshotHash && snapshot.FreshUntil < due)
                    due = snapshot.FreshUntil;
            }
            return due;
        }
        return database.IsInTransaction ? ReadDue() : database.ReadSnapshot(ReadDue);
    }

    public bool TryLease(Guid tenantId, Guid sourceId, string subject, Guid runId, DateTimeOffset now) => database.Transaction(() =>
    {
        ValidateSubject(subject);
        if (runId == Guid.Empty) throw new ArgumentException("A PIP synchronization lease needs a run identifier.");
        var source = ReadSource(tenantId, sourceId);
        if (!source.Enabled || source.Educational || source.TimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("PIP source is unavailable for synchronization.");
        var key = SnapshotKey(tenantId, subject, sourceId);
        var existing = database.Read<PipSyncLease>(LeaseDocumentKind, key);
        if (existing is not null && existing.ExpiresAt > now) return false;
        database.Put(LeaseDocumentKind, key, new PipSyncLease(runId, now.AddSeconds(source.TimeoutSeconds + 30)));
        return true;
    });

    public void ReleaseLease(Guid tenantId, Guid sourceId, string subject, Guid runId) => database.Transaction(() =>
    {
        ValidateSubject(subject);
        var key = SnapshotKey(tenantId, subject, sourceId);
        var existing = database.Read<PipSyncLease>(LeaseDocumentKind, key);
        if (existing?.RunId == runId) database.Put(LeaseDocumentKind, key, new PipSyncLease(Guid.Empty, DateTimeOffset.MinValue));
        return true;
    });
}
