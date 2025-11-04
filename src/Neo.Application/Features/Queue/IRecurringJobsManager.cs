using System.Linq.Expressions;

namespace Neo.Application.Features.Queue;

public interface IRecurringJobsManager
{
    void RemoveIfExists(string recurringJobId);

    void AddOrUpdate<TRecurringJob>()
        where TRecurringJob : IRecurringJob
    {
        var attribute = typeof(TRecurringJob).GetAttributeDeep<RecurringJobAttribute>()
            ?? throw new NotImplementedException("RecurringJob Not Implemented");
        AddOrUpdate<TRecurringJob>(
                typeof(TRecurringJob).Name,
                r => r.Run(),
                attribute.CronJob,
                attribute.Queue,
                attribute.TimeZoneName
                );
    }

    [Telemetry]
    void AddOrUpdate<TRecurringJob>(
        string recurringJobId, Expression<Func<TRecurringJob, Task>> methodCall,
        string cronExpression,
        string queue = "default",
        string timeZoneName = null!
        ) where TRecurringJob : IRecurringJob;

    void Set(string jobName, DateTime dateTime);

    DateTime? Get(string jobName);
}
