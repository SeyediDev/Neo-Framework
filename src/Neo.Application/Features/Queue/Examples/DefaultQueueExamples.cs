namespace Neo.Application.Features.Queue.Examples;

/// <summary>
/// Examples demonstrating the usage of DefaultQueue constant.
/// </summary>
public class DefaultQueueExamples
{
    private readonly IJobExecuter _jobExecuter;

    public DefaultQueueExamples(IJobExecuter jobExecuter)
    {
        _jobExecuter = jobExecuter;
    }

    /// <summary>
    /// Demonstrates how the DefaultQueue constant is used internally.
    /// </summary>
    public void DemonstrateDefaultQueueUsage()
    {
        // Example 1: When queue is null, internally uses DefaultQueue constant
        var jobId1 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            null! // Internally converted to DefaultQueue constant
        );
        Console.WriteLine($"Job with null queue (uses DefaultQueue): {jobId1}");

        // Example 2: When queue is empty, internally uses DefaultQueue constant
        var jobId2 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            "" // Internally converted to DefaultQueue constant
        );
        Console.WriteLine($"Job with empty queue (uses DefaultQueue): {jobId2}");

        // Example 3: When queue is whitespace, internally uses DefaultQueue constant
        var jobId3 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            "   " // Internally converted to DefaultQueue constant
        );
        Console.WriteLine($"Job with whitespace queue (uses DefaultQueue): {jobId3}");

        // Example 4: When queue is specified, uses the specified queue
        var jobId4 = _jobExecuter.Enqueue<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            "email-queue" // Uses specified queue
        );
        Console.WriteLine($"Job with specified queue: {jobId4}");
    }

    /// <summary>
    /// Demonstrates different queue scenarios.
    /// </summary>
    public void DemonstrateQueueScenarios()
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
            "" // Will use DefaultQueue constant internally
        );

        Console.WriteLine($"High priority job: {highPriorityJob}");
        Console.WriteLine($"Low priority job: {lowPriorityJob}");
        Console.WriteLine($"Default queue job: {defaultJob}");
    }
}
