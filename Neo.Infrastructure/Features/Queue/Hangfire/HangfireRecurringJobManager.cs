using Neo.Application.Features.Queue;
using Neo.Domain.Features.Cache;
using Hangfire;
using Hangfire.Storage;
using System.Linq.Expressions;
using System.Reflection;

namespace Neo.Infrastructure.Features.Queue.Hangfire;
public class HangfireRecurringJobManager(
    IRecurringJobManager recurringJobManager, ICacheService cachingProvider)
    : IRecurringJobsManager
{
    public void RemoveIfExists(string recurringJobId)
    {
        recurringJobManager.RemoveIfExists(recurringJobId);
    }
    
    public void AddOrUpdate<TRecurringJob>(
        string recurringJobId,
        Expression<Func<TRecurringJob, Task>> methodCall,
        string cronExpression,
        string queue = "default",
        string timeZoneName = "Iran Standard Time"
        ) where TRecurringJob : IRecurringJob
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneName);
        
        // Get attribute for additional settings
        var attribute = typeof(TRecurringJob).GetCustomAttribute<RecurringJobAttribute>();
        
        RecurringJobOptions options = new()
        {
            TimeZone = timeZone
        };

        // Add description if available - Hangfire doesn't support ExtendedData in RecurringJobOptions
        // Description will be handled by Hangfire's built-in job metadata
        RecurringJob.AddOrUpdate(recurringJobId, methodCall, cronExpression, options);
    }

    public void Set(string jobName, DateTime dateTime)
    {
        string key = CachingKey(jobName);
        cachingProvider.Set(key, dateTime);
    }

    public DateTime? Get(string jobName)
    {
        string key = CachingKey(jobName);
        DateTime? ret;
        try
        {
            ret = cachingProvider.Get<DateTime>(key);
        }
        catch
        {
            try
            {
                string? s = cachingProvider.Get<string>(key);
                _ = DateTime.TryParse(s, out DateTime ret1);
                ret = ret1;
            }
            catch { ret = null; }
        }
        if (ret == null || ret == default(DateTime))
        {
            ret = GetLastDateTime(jobName);
        }

        return ret == null || ret == default(DateTime) ? null : ret;
    }

    private static DateTime? GetLastDateTime(string recurringJobId)
    {
        (DateTime? lastExecutionDateTime, _) = GetExecutionDateTimes(recurringJobId);
        return lastExecutionDateTime;
    }

    private static (DateTime? lastExecutionDateTime, DateTime? nextExecutionDateTime)
        GetExecutionDateTimes(string recurringJobId)
    {
        DateTime? lastExecutionDateTime = null;
        DateTime? nextExecutionDateTime = null;
        using (IStorageConnection connection = JobStorage.Current.GetConnection())
        {
            RecurringJobDto? job = connection.GetRecurringJobs().FirstOrDefault(p => p.Id == recurringJobId);
            if (job != null && job.LastExecution.HasValue)
            {
                lastExecutionDateTime = job.LastExecution;
            }

            if (job != null && job.NextExecution.HasValue)
            {
                nextExecutionDateTime = job.NextExecution;
            }
        }
        return (lastExecutionDateTime, nextExecutionDateTime);
    }

    private string CachingKey(string jobName)
    {
        return $"{nameof(IRecurringJobsManager)}.{jobName}";
    }
}