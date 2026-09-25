using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Domain.Entities.Common;

namespace Neo.Infrastructure.Features.Outbox;

/// <summary>Use the same scoped DbContext in handlers. External HTTP effects still require provider idempotency.</summary>
public sealed class EfOutboxStore<TContext>(TContext db) : IOutboxDeliveryStore where TContext : DbContext
{
    private DbSet<OutboxMessage> Rows => db.Set<OutboxMessage>();
    private IQueryable<OutboxMessage> Dispatchable => Rows.Where(x => !x.IsDeleted &&
        ((x.OutboxState == OutboxState.Requested || x.OutboxState == OutboxState.Retrying || x.OutboxState == OutboxState.ExecutionRetrying)
            && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= DateTime.UtcNow)
        || (x.OutboxState == OutboxState.Dispatching || x.OutboxState == OutboxState.Processing) && x.DeliveryLeaseUntilUtc <= DateTime.UtcNow));
    public async Task AddAsync(OutboxMessage row, CancellationToken ct) { Rows.Add(row); await db.SaveChangesAsync(ct); }
    public async Task UpdateAsync(OutboxMessage row, CancellationToken ct) { Rows.Update(row); await db.SaveChangesAsync(ct); }
    public void UpdateOnly(OutboxMessage row) => Rows.Update(row);
    public async Task SaveChangesAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);
    public async Task FinishAsync(OutboxMessage row, CancellationToken ct) { row.IsDeleted = true; row.ExpireDate = DateTime.UtcNow; await UpdateAsync(row, ct); }
    public Task<OutboxMessage?> GetAsync(long id, CancellationToken ct) => Rows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    public async Task<MessageState?> GetStatusAsync(long id, CancellationToken ct)
    { var row = await GetAsync(id, ct); return row is null ? null : new(row.OutboxState, row.JobId); }
    public async Task<OutboxResponse?> GetOutboxResponseAsync(long id, CancellationToken ct)
    { var row = await GetAsync(id, ct); return row is null ? null : new(row.Id, row.OutboxState, row.JobId, row.IdempotencyKey); }
    public async Task<IEnumerable<OutboxMessage>> GetRequested(int batchSize, CancellationToken ct)
    {
        // Crashing on the final attempt must be terminal, not an endless redispatch loop.
        await Rows.Where(x => !x.IsDeleted &&
            (x.OutboxState == OutboxState.Processing && x.ProcessTryCount >= 3 ||
             x.OutboxState == OutboxState.Dispatching && x.PublishTryCount >= 3) && x.DeliveryLeaseUntilUtc <= DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, OutboxState.Failed)
                .SetProperty(x => x.ProcessError, "Final delivery lease expired; reconcile before replay.")
                .SetProperty(x => x.DeliveryLeaseId, (Guid?)null).SetProperty(x => x.DeliveryLeaseUntilUtc, (DateTime?)null), ct);
        return await Dispatchable.AsNoTracking().OrderBy(x => x.Id).Take(Math.Clamp(batchSize, 1, 100)).ToListAsync(ct);
    }
    public async Task<OutboxMessage?> ClaimDispatchAsync(long id, Guid leaseId, TimeSpan lease, CancellationToken ct)
    {
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        var seconds = lease.TotalSeconds;
        var changed = await Dispatchable.Where(x => x.Id == id && (x.ProcessTryCount ?? 0) < 3 &&
            (x.OutboxState != OutboxState.Dispatching || (x.PublishTryCount ?? 0) < 3)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.OutboxState, OutboxState.Dispatching).SetProperty(x => x.DeliveryLeaseId, leaseId)
            .SetProperty(x => x.DeliveryLeaseUntilUtc, x => DateTime.UtcNow.AddSeconds(seconds))
            .SetProperty(x => x.PublishTryCount, x => (x.PublishTryCount ?? 0) + 1), ct);
        return changed == 1 ? await GetAsync(id, ct) : null;
    }
    public async Task CompleteDispatchAsync(long id, Guid leaseId, string? jobId, string? error, CancellationToken ct)
    {
        var ok = !string.IsNullOrWhiteSpace(jobId); error = Limit(error);
        await Rows.Where(x => x.Id == id && x.OutboxState == OutboxState.Dispatching && x.DeliveryLeaseId == leaseId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, x => ok ? OutboxState.Queued : (x.PublishTryCount >= 3 ? OutboxState.Failed : OutboxState.Retrying))
                .SetProperty(x => x.JobId, jobId).SetProperty(x => x.PublishError, error)
                .SetProperty(x => x.NextAttemptAtUtc, x => ok ? (DateTime?)null : DateTime.UtcNow.AddSeconds(30))
                .SetProperty(x => x.DeliveryLeaseId, (Guid?)null).SetProperty(x => x.DeliveryLeaseUntilUtc, (DateTime?)null), ct);
    }
    public async Task<OutboxMessage?> ClaimExecutionAsync(long id, Guid leaseId, TimeSpan lease, CancellationToken ct)
    {
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        var seconds = lease.TotalSeconds;
        var changed = await Rows.Where(x => x.Id == id && !x.IsDeleted && (x.ProcessTryCount ?? 0) < 3 &&
            (x.OutboxState == OutboxState.Queued || x.OutboxState == OutboxState.Dispatching ||
             x.OutboxState == OutboxState.ExecutionRetrying && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= DateTime.UtcNow) ||
             x.OutboxState == OutboxState.Processing && x.DeliveryLeaseUntilUtc <= DateTime.UtcNow))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, OutboxState.Processing).SetProperty(x => x.DeliveryLeaseId, leaseId)
                .SetProperty(x => x.DeliveryLeaseUntilUtc, x => DateTime.UtcNow.AddSeconds(seconds))
                .SetProperty(x => x.ProcessTryCount, x => (x.ProcessTryCount ?? 0) + 1), ct);
        return changed == 1 ? await GetAsync(id, ct) : null;
    }
    public async Task ExecuteClaimedAsync(long id, Guid leaseId, Func<OutboxMessage,CancellationToken,Task> handler, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await Rows.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OutboxState == OutboxState.Processing &&
                x.DeliveryLeaseId == leaseId && x.DeliveryLeaseUntilUtc > DateTime.UtcNow, ct)
                ?? throw new InvalidOperationException("Execution lease expired or was replaced.");
            await handler(row, ct);
            await db.SaveChangesAsync(ct);
            var changed = await Rows.Where(x => x.Id == id && x.OutboxState == OutboxState.Processing && x.DeliveryLeaseId == leaseId && x.DeliveryLeaseUntilUtc > DateTime.UtcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, OutboxState.Processed).SetProperty(x => x.ProcessError, (string?)null)
                    .SetProperty(x => x.DeliveryLeaseId, (Guid?)null).SetProperty(x => x.DeliveryLeaseUntilUtc, (DateTime?)null), ct);
            if (changed != 1) throw new InvalidOperationException("Execution lease lost; business transaction was not committed.");
            await transaction.CommitAsync(ct);
        }
        catch (Exception error)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await transaction.RollbackAsync(cleanup.Token);
                await transaction.DisposeAsync();
                db.ChangeTracker.Clear();
                var message = Limit(error.Message);
                await Rows.Where(x => x.Id == id && x.OutboxState == OutboxState.Processing && x.DeliveryLeaseId == leaseId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, x => x.ProcessTryCount >= 3 ? OutboxState.Failed : OutboxState.ExecutionRetrying)
                        .SetProperty(x => x.ProcessError, message).SetProperty(x => x.NextAttemptAtUtc, x => DateTime.UtcNow.AddSeconds(30))
                        .SetProperty(x => x.DeliveryLeaseId, (Guid?)null).SetProperty(x => x.DeliveryLeaseUntilUtc, (DateTime?)null), cleanup.Token);
            }
            catch (Exception cleanupError) { error.Data["OutboxCleanupError"] = cleanupError.Message; }
            throw;
        }
    }
    private static string? Limit(string? value) => value is { Length: > 500 } ? value[..500] : value;
}

public static class EfOutboxRegistration
{
    /// <summary>Register after AddNeoApplicationServices. Add OutboxMessage to the context model and apply reviewed migrations first.</summary>
    public static IServiceCollection AddNeoEfOutbox<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.AddScoped<EfOutboxStore<TContext>>();
        services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfOutboxStore<TContext>>());
        services.AddScoped<IOutboxDeliveryStore>(sp => sp.GetRequiredService<EfOutboxStore<TContext>>());
        services.AddScoped<IOutboxExecutionJob, OutboxExecutionJob>();
        return services;
    }
}
