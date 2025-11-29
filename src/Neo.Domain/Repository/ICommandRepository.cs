using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace Neo.Domain.Repository;

public interface ICommandRepository<TEntity> : ICommandRepository<TEntity, int>
	where TEntity : class, IEntity<int>, new()
{ 
}

public interface ICommandRepository<TEntity, TKey> : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
{
    Task<TEntity?> GetAsync(TKey id, CancellationToken cancellationToken);
    void Add(TEntity entity);
    Task AddAsync(TEntity entity)=> AddAsync(entity, CancellationToken.None);
	Task AddAsync(TEntity entity, CancellationToken cancellationToken);
    Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken);
    void Update(TEntity entity);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="updateExpression"></param>
    /// <returns>The total number of rows updated in the database.</returns>
    Task<int> ExecuteUpdateAsync(
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<SetPropertyCalls<TEntity>, SetPropertyCalls<TEntity>>> updateExpression, CancellationToken cancellationToken = default);
    void Remove(TEntity entity);
    Task<bool?> RemoveAsync(TKey id);
    Task<TEntity?> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken, 
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);

    Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(Expression<Func<TEntity, TProperty>> include,
        Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);
    Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(
      IEnumerable<Expression<Func<TEntity, object>>> includes, Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken);
    void AddRange(IEnumerable<TEntity> entities);
    void RemoveRange(IEnumerable<TEntity> entities);
	async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)=> await UnitOfWork.SaveChangesAsync(cancellationToken);
}
