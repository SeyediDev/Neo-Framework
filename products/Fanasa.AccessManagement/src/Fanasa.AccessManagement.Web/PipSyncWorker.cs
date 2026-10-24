namespace Fanasa.AccessManagement.Web.Governance;

/// <summary>Opt-in scheduled ingestion. The durable per-subject checkpoint is also the retry clock.</summary>
public sealed class PipSyncWorker(PipStore store, PipSyncService sync, TimeProvider clock, ILogger<PipSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (IsExpected(error))
            {
                logger.LogWarning("PIP scheduled scan could not complete; it will retry on the next scan.");
            }
            try { await Task.Delay(ScanInterval, clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var jobs = store.ScheduledSources()
            .Where(source => source.Enabled && !source.Educational && source.PollIntervalSeconds > 0)
            .SelectMany(source => (source.SyncSubjects ?? []).Select(subject =>
            {
                var due = store.NextDueAt(source, subject, now);
                return due.HasValue ? new ScheduledJob(source.TenantId, source.Id, subject, due.Value) : null;
            }))
            .Where(job => job is not null && job.DueAt <= now).Select(job => job!)
            .OrderBy(job => job.DueAt).ThenBy(job => job.TenantId).ThenBy(job => job.SourceId).ThenBy(job => job.Subject, StringComparer.Ordinal)
            .Take(50).ToArray();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (job, token) =>
        {
            try { await sync.SyncAsync(job.TenantId, job.SourceId, job.Subject, token, actor: "pip-worker").ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsExpected(error))
            {
                // Do not log subject identifiers, payloads, endpoints, credentials or exception messages.
                logger.LogWarning("PIP scheduled job could not complete for tenant {TenantId}, source {SourceId}.", job.TenantId, job.SourceId);
            }
        }).ConfigureAwait(false);
        return jobs.Length;
    }

    private static bool IsExpected(Exception error) => error is InvalidOperationException or ArgumentException
        or KeyNotFoundException or InvalidDataException or HttpRequestException;

    private sealed record ScheduledJob(Guid TenantId, Guid SourceId, string Subject, DateTimeOffset DueAt);
}
