using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Microsoft.EntityFrameworkCore;

namespace Neo.Infrastructure.Data.Repository.Ef;

public abstract class EfRepositoryBase<TEntity, TKey>(IUnitOfWork uow)
    : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
    //where TKey : struct
{
    public IUnitOfWork UnitOfWork { get; } = uow;
    protected readonly DbSet<TEntity> _dbSet = (DbSet<TEntity>)uow.SetEntity<TEntity>()
            ?? throw new Exception($"Can not find entity {typeof(TEntity).Name} in DbContext {uow.GetType().Name}.");

}
