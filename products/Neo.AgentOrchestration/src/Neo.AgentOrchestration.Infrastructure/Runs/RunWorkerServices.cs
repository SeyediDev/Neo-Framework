using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Cache;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Telementry;
using Neo.Infrastructure.Features.Cache;
using Neo.Infrastructure.Features.Outbox;

namespace Neo.AgentOrchestration.Infrastructure.Runs;

public static class RunWorkerServices
{
    // Opt-in after host configuration validation. No HTTP callback/provider is
    // registered; only deterministic simulation runs can be executed here.
    public static IServiceCollection AddSimulationRunWorker(this IServiceCollection services)
    {
        services.AddNeoEfOutbox<OrchestrationDbContext>();
        // This product job must retain the executor's shared SQL session;
        // resolving the generic MediatR executor would open another boundary.
        services.RemoveAll<IOutboxExecutionJob>();
        services.AddMemoryCache();
        services.TryAddScoped<ICacheService, MemoryCacheService>();
        services.Configure<TelemetryOptions>(o => o.ApplicationName = "Neo.AgentOrchestration.Worker");
        services.TryAddScoped<IRequesterUser, RunWorkerRequester>();
        services.TryAddScoped<ITelementryObject, TelementryObject>();
        services.TryAddScoped<ITelementryBehaviour, TelementryBehaviour>();
        // Neo's SQL conditional claims, not this process-local compatibility
        // dependency, provide multi-process delivery coordination.
        services.AddSingleton<IDistributedLock, MemoryDistributedLock>();
        services.AddScoped<IProcessOutboxRecurringJob, ProcessOutboxRecurringJob>();
        services.AddScoped<IOutboxJobScheduler, RunOutboxScheduler>();
        services.AddScoped<ISimulationHarness, FakeHarness>();
        services.AddScoped<RunExecutionHandler>();
        services.AddScoped<RunOutboxJob>();
        services.AddHostedService<RunSchedule>();
        return services;
    }
}

public sealed class RunOutboxJob(SqlWorkDeliveryExecutor executor, RunExecutionHandler handler)
{
    [AutomaticRetry(Attempts = 0), Queue("outbox")]
    public async Task Execute(long outboxId, CancellationToken ct)
        => _ = await executor.ExecuteAsync(outboxId, handler.ExecuteAsync, ct);
}

public sealed class RunOutboxScheduler(IBackgroundJobClient client) : IOutboxJobScheduler
{
    public string? ScheduleOnline(object message) => throw new NotSupportedException("Durable run messages are required.");
    public Task<string?> ScheduleOnlineAsync(object message, CancellationToken ct) => throw new NotSupportedException("Durable run messages are required.");
    public string? ScheduleOutboxMessage(OutboxMessage row)
    {
        if (row.Id <= 0 || row.MessageType != typeof(WorkDeliverySignal).FullName)
            throw new InvalidOperationException("Unsupported run outbox envelope.");
        try { return client.Create(Job.FromExpression<RunOutboxJob>(x => x.Execute(row.Id, CancellationToken.None)), new EnqueuedState("outbox")); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new InvalidOperationException("Run queue submission failed; inspect queue connectivity."); }
    }
    public Task<string?> ScheduleOutboxMessageAsync(OutboxMessage row, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(ScheduleOutboxMessage(row)); }
}

internal sealed class RunSchedule(IRecurringJobManager recurring, IBackgroundJobClient client) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        recurring.AddOrUpdate<IProcessOutboxRecurringJob>("neo-agent-orchestration-dispatch", "outbox", x => x.Run(),
            "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        client.Create(Job.FromExpression<IProcessOutboxRecurringJob>(x => x.Run()), new EnqueuedState("outbox"));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
