namespace Neo.Domain.Entities.Base;

public interface IDomainEventEntity
{
    IReadOnlyCollection<BaseEvent> DomainEvents { get; }
    void AddDomainEvent(BaseEvent domainEvent);
    void RemoveDomainEvent(BaseEvent domainEvent);
    void ClearDomainEvents();
    void AddDomainEvents(IEnumerable<BaseEvent> domainEvents)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        foreach (var domainEvent in domainEvents.ToArray())
        {
            AddDomainEvent(domainEvent);
        }
    }
}
