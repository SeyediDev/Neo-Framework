namespace Neo.Domain.Repository;

public interface IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
{
    public IUnitOfWork UnitOfWork { get; }
	IQueryable<TEntity> Query();
}
