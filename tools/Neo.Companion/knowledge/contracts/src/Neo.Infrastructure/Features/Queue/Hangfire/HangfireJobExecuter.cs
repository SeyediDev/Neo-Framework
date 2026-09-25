using Neo.Application.Features.Queue;
using Hangfire;
using System.Linq.Expressions;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

internal class HangfireJobExecuter(IBackgroundJobClient backgroundJobClient) : IJobExecuter
{
    private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
    
    private const string DefaultQueue = "default";

    public Task<string?> EnqueueAsync<TJob>(Expression<Func<TJob, CancellationToken, Task>> methodCall, string queue, CancellationToken ct)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;
        
        ct.ThrowIfCancellationRequested();
        var body = new CancellationArgumentVisitor(methodCall.Parameters[1]).Visit(methodCall.Body)!;
        var serializable = Expression.Lambda<Func<TJob, Task>>(body, methodCall.Parameters[0]);
        string jobId = _backgroundJobClient.Enqueue(queue, serializable);
        return Task.FromResult<string?>(jobId);
    }

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Enqueue(queue, methodCall);
    }

    public string Enqueue(Expression<Action> methodCall, string queue)
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Enqueue(queue, methodCall);
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, TimeSpan delay, string queue)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Schedule(queue, methodCall, delay);
    }

    public string Schedule(Expression<Action> methodCall, TimeSpan delay, string queue)
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Schedule(queue, methodCall, delay);
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, DateTimeOffset enqueueAt, string queue)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Schedule(queue, methodCall, enqueueAt);
    }

    public string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt, string queue)
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.Schedule(queue, methodCall, enqueueAt);
    }

    public bool Requeue(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return _backgroundJobClient.Requeue(jobId);
    }

    public bool Reschedule(string jobId, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return _backgroundJobClient.Reschedule(jobId, delay);
    }

    public bool Reschedule(string jobId, DateTimeOffset enqueueAt)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return _backgroundJobClient.Reschedule(jobId, enqueueAt);
    }

    public bool Delete(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return _backgroundJobClient.Delete(jobId);
    }

    public string ContinueJobWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith(string parentId, Expression<Action> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith(string parentId, Expression<Func<Task>> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith(string parentId, Expression<Action> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith(string parentId, Expression<Func<Task>> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return _backgroundJobClient.ContinueJobWith(parentId, queue, methodCall);
    }
    private sealed class CancellationArgumentVisitor(ParameterExpression token) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == token ? Expression.Constant(CancellationToken.None) : base.VisitParameter(node);
    }
}
