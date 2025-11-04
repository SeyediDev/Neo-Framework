namespace Neo.Application.Features.Queue.Examples;

/// <summary>
/// Examples demonstrating the usage of IJobExecuter methods.
/// </summary>
public class JobExecuterExamples
{
    private readonly IJobExecuter _jobExecuter;

    public JobExecuterExamples(IJobExecuter jobExecuter)
    {
        _jobExecuter = jobExecuter;
    }

    /// <summary>
    /// Demonstrates how to use Schedule methods.
    /// </summary>
    public void DemonstrateScheduleMethods()
    {
        // Schedule with TimeSpan delay
        var jobId1 = _jobExecuter.Schedule<EmailJob>(
            job => job.SendEmailAsync("user@example.com", "Subject", "Body"),
            TimeSpan.FromMinutes(5),
            "email-queue"
        );

        // Schedule with DateTimeOffset
        var jobId2 = _jobExecuter.Schedule(
            () => Console.WriteLine("Scheduled task"),
            DateTimeOffset.Now.AddHours(1),
            "default-queue"
        );

        Console.WriteLine($"Scheduled job 1: {jobId1}");
        Console.WriteLine($"Scheduled job 2: {jobId2}");
    }

    /// <summary>
    /// Demonstrates how to use Requeue method.
    /// </summary>
    public void DemonstrateRequeueMethod()
    {
        var jobId = "some-job-id";
        var requeued = _jobExecuter.Requeue(jobId);
        
        if (requeued)
        {
            Console.WriteLine($"Job {jobId} requeued successfully");
        }
        else
        {
            Console.WriteLine($"Failed to requeue job {jobId}");
        }
    }

    /// <summary>
    /// Demonstrates how to use Reschedule methods.
    /// </summary>
    public void DemonstrateRescheduleMethods()
    {
        var jobId = "some-job-id";
        
        // Reschedule with TimeSpan delay
        var rescheduled1 = _jobExecuter.Reschedule(jobId, TimeSpan.FromMinutes(10));
        
        // Reschedule with DateTimeOffset
        var rescheduled2 = _jobExecuter.Reschedule(jobId, DateTimeOffset.Now.AddDays(1));
        
        Console.WriteLine($"Rescheduled with delay: {rescheduled1}");
        Console.WriteLine($"Rescheduled with date: {rescheduled2}");
    }

    /// <summary>
    /// Demonstrates how to use Delete method.
    /// </summary>
    public void DemonstrateDeleteMethod()
    {
        var jobId = "some-job-id";
        var deleted = _jobExecuter.Delete(jobId);
        
        if (deleted)
        {
            Console.WriteLine($"Job {jobId} deleted successfully");
        }
        else
        {
            Console.WriteLine($"Failed to delete job {jobId}");
        }
    }

    /// <summary>
    /// Demonstrates how to use ContinueJobWith methods.
    /// </summary>
    public void DemonstrateContinueJobWithMethods()
    {
        var parentJobId = "parent-job-id";
        
        // Continue with Action
        var continuationId1 = _jobExecuter.ContinueJobWith<EmailJob>(
            parentJobId,
            job => job.SendNotificationAsync("Task completed"),
            "notification-queue"
        );

        // Continue with Func<Task>
        var continuationId2 = _jobExecuter.ContinueJobWith(
            parentJobId,
            () => Task.Run(() => Console.WriteLine("Continuation task")),
            "default-queue"
        );

        Console.WriteLine($"Continuation job 1: {continuationId1}");
        Console.WriteLine($"Continuation job 2: {continuationId2}");
    }

    /// <summary>
    /// Demonstrates how to use ContinueWith methods.
    /// </summary>
    public void DemonstrateContinueWithMethods()
    {
        var parentJobId = "parent-job-id";
        
        // Continue with Action
        var continuationId1 = _jobExecuter.ContinueWith<EmailJob>(
            parentJobId,
            job => job.SendNotificationAsync("Task completed"),
            "notification-queue"
        );

        // Continue with Func<Task>
        var continuationId2 = _jobExecuter.ContinueWith(
            parentJobId,
            () => Task.Run(() => Console.WriteLine("Continuation task")),
            "default-queue"
        );

        Console.WriteLine($"Continue with job 1: {continuationId1}");
        Console.WriteLine($"Continue with job 2: {continuationId2}");
    }
}

// Example job classes
public class EmailJob : IJob
{
    public Task SendEmailAsync(string to, string subject, string body)
    {
        Console.WriteLine($"Sending email to {to}: {subject}");
        return Task.CompletedTask;
    }

    public Task SendUrgentEmailAsync(string to, string subject, string body)
    {
        Console.WriteLine($"Sending URGENT email to {to}: {subject}");
        return Task.CompletedTask;
    }

    public Task SendNotificationAsync(string message)
    {
        Console.WriteLine($"Sending notification: {message}");
        return Task.CompletedTask;
    }
}
