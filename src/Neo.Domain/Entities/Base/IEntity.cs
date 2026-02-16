namespace Neo.Domain.Entities.Base;

public interface IEntity
{
}

public interface IEntity<TKey> : IEntity
{
    public TKey Id { get; set; }
}