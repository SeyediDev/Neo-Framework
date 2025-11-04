using System.ComponentModel;
using Neo.Common.Attributes;

namespace Neo.Domain.Entities.Base;
public enum DomainProvider { Domain }
public enum DomainSchema { CoreCommon, CoreConfig, Core, CoreLog }

[DataProvider(nameof(DomainProvider.Domain))]
public abstract class BaseEntity<TKey> : IEntity<TKey>, IDomainEventEntity
//where TKey : struct
{
    // This can easily be modified to be BaseEntity<T> and public T Id to support different key types.
    // Using non-generic integer types for simplicity
    [DisplayName("شناسه")]
    public TKey Id { get; set; } = default!;

    private readonly List<BaseEvent> _domainEvents = [];

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyCollection<BaseEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(BaseEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void RemoveDomainEvent(BaseEvent domainEvent)
    {
        _domainEvents.Remove(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}
