namespace Neo.Application.Features.Outbox.Implementation;

public class ProcessOutboxRecurringJob(
    IOutboxStore outboxStore,
    IOutboxJobScheduler scheduler,
    IDistributedLock distributedLock,
    ILogger<ProcessOutboxRecurringJob> logger) : IProcessOutboxRecurringJob
{
    public async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        if (outboxStore is IOutboxDeliveryStore delivery)
        {
            foreach (var candidate in await delivery.GetRequested(15, timeout.Token))
            {
                var token = Guid.NewGuid();
                var row = await delivery.ClaimDispatchAsync(candidate.Id, token, TimeSpan.FromMinutes(5), timeout.Token);
                if (row is null) continue;
                string? jobId = null;
                string? error = null;
                try
                {
                    jobId = await scheduler.ScheduleOutboxMessageAsync(row, timeout.Token);
                    if (string.IsNullOrWhiteSpace(jobId)) error = "Queue returned no job ID.";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    error = ex.Message;
                    logger.LogError(ex, "Outbox dispatch failed for {OutboxId}", row.Id);
                }
                await delivery.CompleteDispatchAsync(row.Id, token, jobId, error, timeout.Token);
                if (error is not null) logger.LogWarning("Outbox {OutboxId} requires retry or review: {Error}", row.Id, error);
            }
            return;
        }

        // Compatibility path: legacy stores do not provide atomic claims or execution tracking.
        await distributedLock.ExecuteWithLockAsync("process_outbox_recurring_job", TimeSpan.FromMinutes(5), async () =>
        {
            foreach (var row in await outboxStore.GetRequested(15, timeout.Token))
            {
                try
                {
                    row.JobId = await scheduler.ScheduleOutboxMessageAsync(row, timeout.Token);
                    if (string.IsNullOrWhiteSpace(row.JobId)) throw new InvalidOperationException("Queue returned no job ID.");
                    row.OutboxState = OutboxState.Queued;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    row.PublishTryCount = (row.PublishTryCount ?? 0) + 1;
                    row.OutboxState = row.PublishTryCount >= 3 ? OutboxState.Failed : OutboxState.Retrying;
                    row.PublishError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    logger.LogError(ex, "Legacy outbox dispatch failed for {OutboxId}", row.Id);
                }
                await outboxStore.UpdateAsync(row, timeout.Token);
            }
        });
    }
}
