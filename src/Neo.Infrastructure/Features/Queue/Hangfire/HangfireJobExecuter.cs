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
        
        // Hangfire نمی‌تواند CancellationToken runtime را serialize کند، پس از None استفاده می‌کنیم
        Expression<Action<TJob>> wrapper = job => methodCall.Compile()(job, CancellationToken.None).GetAwaiter().GetResult();

        string jobId = BackgroundJob.Enqueue(queue, wrapper);
        return Task.FromResult<string?>(jobId);
    }

    public string Enqueue<TJob>(Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.Enqueue(queue, methodCall);
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

        return BackgroundJob.Schedule(queue, methodCall, delay);
    }

    public string Schedule(Expression<Action> methodCall, TimeSpan delay, string queue)
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.Schedule(queue, methodCall, delay);
    }

    public string Schedule<TJob>(Expression<Func<TJob, Task>> methodCall, DateTimeOffset enqueueAt, string queue)
        where TJob : IJob
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.Schedule(queue, methodCall, enqueueAt);
    }

    public string Schedule(Expression<Action> methodCall, DateTimeOffset enqueueAt, string queue)
    {
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.Schedule(queue, methodCall, enqueueAt);
    }

    public bool Requeue(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return BackgroundJob.Requeue(jobId);
    }

    public bool Reschedule(string jobId, TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return BackgroundJob.Reschedule(jobId, delay);
    }

    public bool Reschedule(string jobId, DateTimeOffset enqueueAt)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return BackgroundJob.Reschedule(jobId, enqueueAt);
    }

    public bool Delete(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID must be provided", nameof(jobId));

        return BackgroundJob.Delete(jobId);
    }

    public string ContinueJobWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith(string parentId, Expression<Action> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueJobWith(string parentId, Expression<Func<Task>> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith<TJob>(string parentId, Expression<Action> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith(string parentId, Expression<Action> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        return BackgroundJob.ContinueJobWith(parentId, queue, methodCall);
    }

    public string ContinueWith<TJob>(string parentId, Expression<Func<TJob, Task>> methodCall, string queue)
        where TJob : IJob
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        // Convert Func<TJob, Task> to Action<TJob>
        Expression<Action<TJob>> actionExpression = job => methodCall.Compile()(job).GetAwaiter().GetResult();
        return BackgroundJob.ContinueJobWith(parentId, queue, actionExpression);
    }

    public string ContinueWith(string parentId, Expression<Func<Task>> methodCall, string queue)
    {
        if (string.IsNullOrWhiteSpace(parentId)) throw new ArgumentException("Parent job ID must be provided", nameof(parentId));
        ArgumentNullException.ThrowIfNull(methodCall);
        queue = string.IsNullOrWhiteSpace(queue) ? DefaultQueue : queue;

        // Convert Func<Task> to Action
        Expression<Action> actionExpression = () => methodCall.Compile()().GetAwaiter().GetResult();
        return BackgroundJob.ContinueJobWith(parentId, queue, actionExpression);
    }
}
