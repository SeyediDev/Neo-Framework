using System.Linq.Expressions;
using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Neo.Infrastructure.Data.Repository.Ef;
public abstract class EfCommandRepository<TEntity, TKey, TCommandUnitOfWork>(TCommandUnitOfWork uow)
    : EfRepositoryBase<TEntity, TKey>(uow), ICommandRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
    where TCommandUnitOfWork : IUnitOfWork
{
    public async Task<TEntity?> GetAsync(TKey id, CancellationToken cancellationToken)
    {
        return await _dbSet.FindAsync([id], cancellationToken);
    }

    public async Task<TEntity?> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (orderBy!=null)
            query = orderBy(query);
        return await query.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(
       Expression<Func<TEntity, TProperty>> include, Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken, 
       Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet.Include(include);
        if (orderBy != null)
            query = orderBy(query);
        return await query.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(
      IEnumerable<Expression<Func<TEntity, object>>> includes, Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken)
    {
        IQueryable<TEntity> query = _dbSet;
        if (includes != null)
        {
            foreach (var include in includes)
            {
                query = query.Include(include);
            }
        }
        return await _dbSet.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public async Task AddAsync(TEntity entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public void Add(TEntity entity)
    {
        _dbSet.Add(entity);
    }

    public void AddRange(IEnumerable<TEntity> entities)
    {
        _dbSet.AddRange(entities);
    }

    public void RemoveRange(IEnumerable<TEntity> entities)
    {
        _dbSet.RemoveRange(entities);
    }

    public async Task AddRangeAsync(IEnumerable<TEntity> entities)
    {
        await _dbSet.AddRangeAsync(entities);
    }

    public void Update(TEntity entity)
    {
        _dbSet.Update(entity);
    }

    public void Remove(TEntity entity)
    {
        _dbSet.Remove(entity);
    }

    public async Task<bool?> RemoveAsync(TKey id)
    {
        var entity = await _dbSet.FindAsync(id);
        if (entity != null)
        {
            Remove(entity);
            return true;
        }
        return null;
    }

    public async Task<int> ExecuteUpdateAsync(
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<SetPropertyCalls<TEntity>, SetPropertyCalls<TEntity>>> updateExpression, CancellationToken cancellationToken=default)
    {
        return await _dbSet.Where(predicate).ExecuteUpdateAsync(updateExpression, cancellationToken);
    }  
}
