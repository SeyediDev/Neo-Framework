using System.Linq.Expressions;

namespace Neo.Application.Features.Queue;

public interface IJobExecuter
{
    [Telemetry]
    Task<string?> EnqueueAsync<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, string queue, CancellationToken ct)
        where TJob : IJob;
    [Telemetry]
    string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob;
    [Telemetry]
    string Enqueue(Expression<Action> methodCall, string queue);
    
    [Telemetry]
    string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay, string queue)
        where TJob : IJob;
    [Telemetry]
    string Schedule(Expression<Action> methodCall, TimeSpan delay, string queue);
    [Telemetry]
    string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, DateTimeOffset enqueueAt, string queue)
        where TJob : IJob;
    [Telemetry]
    string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt, string queue);
    
    [Telemetry]
    bool Requeue(string jobId);
    
    [Telemetry]
    bool Reschedule(string jobId, TimeSpan delay);
    [Telemetry]
    bool Reschedule(string jobId, DateTimeOffset enqueueAt);
    
    [Telemetry]
    bool Delete(string jobId);
    
    [Telemetry]
    string ContinueJobWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob;
    [Telemetry]
    string ContinueJobWith(string parentId, Expression<Action> methodCall, string queue);
    [Telemetry]
    string ContinueJobWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob;
    [Telemetry]
    string ContinueJobWith(string parentId, Expression<Func<Task>> methodCall, string queue);
    
    [Telemetry]
    string ContinueWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob;
    [Telemetry]
    string ContinueWith(string parentId, Expression<Action> methodCall, string queue);
    [Telemetry]
    string ContinueWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob;
    [Telemetry]
    string ContinueWith(string parentId, Expression<Func<Task>> methodCall, string queue);
}