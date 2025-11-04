namespace Neo.Application.Features.Queue;

public interface IRecurringJob : IJob
{
    [Telemetry("recurring_job", "run")]
    Task Run();
}

[AttributeUsage(AttributeTargets.Interface)]
public class RecurringJobAttribute(
    string cronJob, 
    string queue, 
    string timeZoneName = "Iran Standard Time"
    ) : Attribute
{ 
    public string CronJob { get; set; } = cronJob;
    public string Queue { get; set; } = queue;
    public string TimeZoneName { get; set; } = timeZoneName;
}