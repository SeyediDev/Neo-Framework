using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Neo.Application.Features.Queue.Implementation.NoOp;

public sealed class NoOpRecurringJobsManager : IRecurringJobsManager
{
    private readonly ConcurrentDictionary<string, DateTime> _scheduled = new();

    public void RemoveIfExists(string recurringJobId) => _scheduled.TryRemove(recurringJobId, out _);

    public void AddOrUpdate<TRecurringJob>(string recurringJobId, Expression<Func<TRecurringJob, Task>> methodCall, string cronExpression, string queue = "default", string timeZoneName = null!)
        where TRecurringJob : IRecurringJob
    {
        _scheduled[recurringJobId] = DateTime.UtcNow;
    }

    public void Set(string jobName, DateTime dateTime) => _scheduled[jobName] = dateTime;

    public DateTime? Get(string jobName) => _scheduled.TryGetValue(jobName, out var dateTime) ? dateTime : null;
}