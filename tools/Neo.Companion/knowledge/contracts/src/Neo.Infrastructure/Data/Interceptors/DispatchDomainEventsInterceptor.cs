using Neo.Domain.Entities.Base;
using MediatR;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Neo.Infrastructure.Data.Interceptors;

public class DispatchDomainEventsInterceptor(IMediator mediator) : SaveChangesInterceptor
{
    private const int MaxEventsPerSave = 1024;
    private readonly ConditionalWeakTable<DbContext, DispatchState> pending = new();
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        DispatchDomainEvents(eventData.Context).GetAwaiter().GetResult();

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await DispatchDomainEvents(eventData.Context, cancellationToken);

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // Local handlers run before persistence. Publish external effects through the transaction's outbox.
    // After failure, discard the unit of work; in-memory handler mutations are not rolled back.
    public async Task DispatchDomainEvents(DbContext? context, CancellationToken cancellationToken = default)
    {
        if (context is null) return;
        var state = pending.GetValue(context, _ => new DispatchState());
        if (state.Dispatching)
            throw new InvalidOperationException("A domain event handler must not call SaveChanges recursively. Stage changes in the current unit of work instead.");
        state.Dispatching = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = context.ChangeTracker.Entries<IDomainEventEntity>()
                    .SelectMany(e => e.Entity.DomainEvents.Select(ev => (Entity: e.Entity, Event: ev)))
                    .Where(pair => !state.Published.Any(p => ReferenceEquals(p.Entity, pair.Entity) && ReferenceEquals(p.Event, pair.Event)))
                    .ToArray();
                if (batch.Length == 0) break;
                foreach (var pair in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (state.Published.Count >= MaxEventsPerSave)
                        throw new InvalidOperationException("Domain event cascade exceeded 1024 events in one save. Check for a cycle.");
                    await mediator.Publish((INotification)pair.Event, cancellationToken);
                    state.Published.Add(pair);
                }
            }
        }
        catch { pending.Remove(context); throw; }
        finally { state.Dispatching = false; }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Accept(eventData.Context);
        return result;
    }
    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Accept(eventData.Context);
        return ValueTask.FromResult(result);
    }
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }
    public override void SaveChangesCanceled(DbContextEventData eventData) => Forget(eventData.Context);
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }
    private void Accept(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var state)) return;
        foreach (var pair in state.Published) pair.Entity.RemoveDomainEvent(pair.Event);
        pending.Remove(context);
    }
    private void Forget(DbContext? context)
    {
        if (context is not null) pending.Remove(context);
    }
    private sealed class DispatchState
    {
        public bool Dispatching { get; set; }
        public List<(IDomainEventEntity Entity, BaseEvent Event)> Published { get; } = [];
    }
}
