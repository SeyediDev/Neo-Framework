namespace Neo.Application.Features.Outbox.Implementation;

public class ProcessOutboxRecurringJob(
    IOutboxStore outboxStore,
    IOutboxJobScheduler outboxJobScheduler,
    IDistributedLock distributedLock,
    ILogger<ProcessOutboxRecurringJob> logger
    ) : IProcessOutboxRecurringJob
{
    private const int BatchSize = 15;
    private const int TimeoutSeconds = 30;
    private const string LockKey = "process_outbox_recurring_job";

    public async Task Run()
    {
        logger.LogInformation("Beginning to process outbox commands");
        
        // Use distributed lock to prevent concurrent execution across multiple pods
        var lockAcquired = await distributedLock.ExecuteWithLockAsync(
            LockKey, 
            TimeSpan.FromSeconds(TimeoutSeconds), 
            ProcessOutboxMessagesAsync);
        
        if (!lockAcquired)
        {
            logger.LogInformation("Could not acquire distributed lock - another instance is already processing outbox messages");
            return;
        }
    }

    private async Task ProcessOutboxMessagesAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        
        try
        {
            var outboxMessages = await outboxStore.GetRequested(BatchSize, cts.Token);
            if (!outboxMessages.Any())
            {
                logger.LogInformation("Completed processing outbox commands - no commands to process");
                return;
            }

            logger.LogInformation("Start processing outbox commands {Count}", outboxMessages.Count());
            
            var successCount = 0;
            var failedCount = 0;
            List<long> failedMessageIds = [];

            foreach (var outboxMessage in outboxMessages)
            {
                try
                {
                    var jobId = await outboxJobScheduler.ScheduleOnlineAsync(outboxMessage, cts.Token);
                    
                    if (!string.IsNullOrEmpty(jobId))
                    {
                        outboxMessage.JobId = jobId;
                        outboxMessage.OutboxState = OutboxState.Queued;
                        successCount++;
                        
                        logger.LogDebug("Successfully scheduled outbox message {MessageId} with job {JobId}", 
                            outboxMessage.Id, jobId);
                    }
                    else
                    {
                        outboxMessage.OutboxState = OutboxState.Failed;
                        outboxMessage.PublishError = "Failed to schedule job - no job ID returned";
                        outboxMessage.PublishTryCount = (outboxMessage.PublishTryCount ?? 0) + 1;
                        failedCount++;
                        failedMessageIds.Add(outboxMessage.Id);
                        
                        logger.LogWarning("Failed to schedule outbox message {MessageId} - no job ID returned", 
                            outboxMessage.Id);
                    }
                }
                catch (Exception ex)
                {
                    outboxMessage.OutboxState = OutboxState.Failed;
                    outboxMessage.PublishError = ex.Message;
                    outboxMessage.PublishTryCount = (outboxMessage.PublishTryCount ?? 0) + 1;
                    failedCount++;
                    failedMessageIds.Add(outboxMessage.Id);
                    
                    logger.LogError(ex, "Error processing outbox message {MessageId}", outboxMessage.Id);
                }
                
                outboxStore.UpdateOnlyAsync(outboxMessage);
            }

            await outboxStore.SaveChangesAsync(cts.Token);
            
            logger.LogInformation("Completed processing outbox commands - " +
                "Success: {SuccessCount}, Failed: {FailedCount}, Total: {TotalCount}",
                successCount, failedCount, outboxMessages.Count());

            if (failedMessageIds.Count != 0)
            {
                logger.LogWarning("Failed message IDs: {FailedMessageIds}", 
                    string.Join(", ", failedMessageIds));
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Outbox processing was cancelled due to timeout");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while processing outbox commands");
            throw;
        }
    }
}