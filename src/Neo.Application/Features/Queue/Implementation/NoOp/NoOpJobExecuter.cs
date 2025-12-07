using System.Linq.Expressions;

namespace Neo.Application.Features.Queue.Implementation.NoOp;

public sealed class NoOpJobExecuter : IJobExecuter
{
    public Task<string?> EnqueueAsync<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, string queue, CancellationToken ct)
        where TJob : IJob => Task.FromResult<string?>(null);

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall, string queue) where TJob : IJob => string.Empty;

    public string Enqueue(Expression<Action> methodCall, string queue) => string.Empty;

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay, string queue) where TJob : IJob => string.Empty;

    public string Schedule(Expression<Action> methodCall, TimeSpan delay, string queue) => string.Empty;

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, DateTimeOffset enqueueAt, string queue) where TJob : IJob => string.Empty;

    public string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt, string queue) => string.Empty;

    public bool Requeue(string jobId) => false;

    public bool Reschedule(string jobId, TimeSpan delay) => false;

    public bool Reschedule(string jobId, DateTimeOffset enqueueAt) => false;

    public bool Delete(string jobId) => false;

    public string ContinueJobWith<TJob>(string parentId, Expression<Action> methodCall, string queue) where TJob : IJob => string.Empty;

    public string ContinueJobWith(string parentId, Expression<Action> methodCall, string queue) => string.Empty;

    public string ContinueJobWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue) where TJob : IJob => string.Empty;

    public string ContinueJobWith(string parentId, Expression<Func<Task>> methodCall, string queue) => string.Empty;

    public string ContinueWith<TJob>(string parentId, Expression<Action> methodCall, string queue) where TJob : IJob => string.Empty;

    public string ContinueWith(string parentId, Expression<Action> methodCall, string queue) => string.Empty;

    public string ContinueWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue) where TJob : IJob => string.Empty;

    public string ContinueWith(string parentId, Expression<Func<Task>> methodCall, string queue) => string.Empty;
}