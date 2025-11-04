namespace Neo.Application.Features.Queue.Examples;

/// <summary>
/// Examples demonstrating queue handling with default fallback.
/// </summary>
public class QueueHandlingExamples
{
    private readonly IJobExecuter _jobExecuter;

    public QueueHandlingExamples(IJobExecuter jobExecuter)
    {
        _jobExecuter = jobExecuter;
    }

    /// <summary>
    /// Demonstrates queue handling with different scenarios.
    /// </summary>
    public void DemonstrateQueueHandling()
    {
        // Example 1: Explicit queue name
        var jobId1 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            "email-queue"
        );
        Console.WriteLine($"Job with explicit queue: {jobId1}");

        // Example 2: Empty string - will use "default"
        var jobId2 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            ""
        );
        Console.WriteLine($"Job with empty queue (default): {jobId2}");

        // Example 3: Null string - will use "default"
        var jobId3 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            null!
        );
        Console.WriteLine($"Job with null queue (default): {jobId3}");

        // Example 4: Whitespace string - will use "default"
        var jobId4 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            "   "
        );
        Console.WriteLine($"Job with whitespace queue (default): {jobId4}");

        // Example 5: Schedule with default queue
        var scheduledJobId = _jobExecuter.Schedule<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            TimeSpan.FromMinutes(5),
            null! // Will use "default"
        );
        Console.WriteLine($"Scheduled job with default queue: {scheduledJobId}");
    }

    /// <summary>
    /// Demonstrates different queue types.
    /// </summary>
    public void DemonstrateDifferentQueues()
    {
        // High priority queue
        var highPriorityJob = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendUrgentEmailAsync("admin@example.com", "Urgent", "Critical issue"),
            "high-priority"
        );

        // Low priority queue
        var lowPriorityJob = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Newsletter", "Monthly newsletter"),
            "low-priority"
        );

        // Default queue (when no queue specified)
        var defaultJob = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Welcome", "Welcome message"),
            "" // Will use "default"
        );

        Console.WriteLine($"High priority job: {highPriorityJob}");
        Console.WriteLine($"Low priority job: {lowPriorityJob}");
        Console.WriteLine($"Default queue job: {defaultJob}");
    }
}
