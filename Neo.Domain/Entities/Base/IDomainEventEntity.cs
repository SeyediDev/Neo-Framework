namespace Neo.Domain.Entities.Base;

public interface IDomainEventEntity
{
    IReadOnlyCollection<BaseEvent> DomainEvents { get; }
    void ClearDomainEvents();
    void AddDomainEvents(IEnumerable<BaseEvent> domainEvents)
    {
        foreach (var domainEvent in domainEvents)
        {
            //TODO
            _ = DomainEvents.Append(domainEvent);
        }
    }
}
