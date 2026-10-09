using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public static class GatewayWorkerRegistration
{
    // Call after the existing product worker registration. No new hosted service,
    // recurring job, execution thread or generic IOutboxStore replacement.
    public static IServiceCollection AddGatewayWorker(this IServiceCollection services)
    {
        services.AddScoped<GatewayOutboxJob>();
        services.AddScoped<EfOutboxStore<GatewayDbContext>>();
        services.AddScoped<GatewayOutboxScheduler>();
        services.AddScoped<ProcessOutboxRecurringJob>();
        services.AddScoped<IProcessOutboxRecurringJob, UnifiedOutboxRecurringJob>();
        return services;
    }
}

public sealed class GatewayOutboxScheduler(IBackgroundJobClient client) : IOutboxJobScheduler
{
    public string? ScheduleOnline(object message) => throw new NotSupportedException();
    public Task<string?> ScheduleOnlineAsync(object message, CancellationToken ct) => throw new NotSupportedException();
    public string? ScheduleOutboxMessage(OutboxMessage row)
    {
        if (row.Id <= 0 || row.MessageType != typeof(GatewayAdvanceSignal).FullName) throw new GatewayJobException();
        try { return client.Create(Job.FromExpression<GatewayOutboxJob>(x => x.Execute(row.Id, CancellationToken.None)), new EnqueuedState("outbox")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new GatewayJobException("gateway-queue-unavailable"); }
    }
    public Task<string?> ScheduleOutboxMessageAsync(OutboxMessage row, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(ScheduleOutboxMessage(row)); }
}

// Same scheduled IProcessOutboxRecurringJob invocation, both independent SQL
// stores through Neo's fenced delivery implementation. SQL owns concurrency.
public sealed class UnifiedOutboxRecurringJob(ProcessOutboxRecurringJob product,
    EfOutboxStore<GatewayDbContext> gateway, GatewayOutboxScheduler scheduler,
    IDistributedLock compatibilityLock, ILogger<ProcessOutboxRecurringJob> logger) : IProcessOutboxRecurringJob
{
    public async Task Run()
    {
        try { await product.Run(); }
        finally { await new ProcessOutboxRecurringJob(gateway, scheduler, compatibilityLock, logger).Run(); }
    }
}
