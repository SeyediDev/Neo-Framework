using System.Linq.Expressions;

namespace Neo.Domain.Repository;

public interface IQueryRepository<TEntity, TKey> : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
{
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken);
    Task<TDto?> GetByIdAsync<TDto>(TKey id, CancellationToken cancellationToken);
    IQueryable<TEntity> GetEntityAsQueryable();
    Task<TEntity?> GetByIdWithIncludeAsync<TProperty>(TKey id, Expression<Func<TEntity, TProperty>> include, CancellationToken cancellationToken);
    Task<IEnumerable<TEntity>> GetAllAsync(CancellationToken cancellationToken, Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null);
    Task<IEnumerable<TEntity>> GetAllWithIncludeAsync<TProperty>(Expression<Func<TEntity, TProperty>> include,
        CancellationToken cancellationToken, Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null);

    Task<IEnumerable<TEntity>> GetAllWithIncludeAsync(IEnumerable<Expression<Func<TEntity, object?>>> includes,
        CancellationToken cancellationToken, Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null);

    Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedAsync(int pageNumber, int pageSize, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);
    Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedWithIncludeAsync<TProperty>(
        Expression<Func<TEntity, TProperty>> include, int pageNumber, int pageSize, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);

    Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedWithIncludeAsync<TProperty>(
       IEnumerable<Expression<Func<TEntity, TProperty>>> includes,
       int pageNumber, int pageSize, CancellationToken cancellationToken,
       Expression<Func<TEntity, bool>>? predicate = null,
       Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);

    Task<List<TDto>> GetAllAsync<TDto>(CancellationToken cancellationToken,
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        where TDto : class, new();
    Task<List<TDto>> GetAllAsync<TDto>(int pageNumber, int pageSize, CancellationToken cancellationToken,
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        where TDto : class, new();

    Task<bool> AnyAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken);
    Task<TEntity?> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);
    Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(Expression<Func<TEntity, TProperty>> include,
        Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null);
    Task<TEntity?> FirstOrDefaultWithIncludeAsync<TProperty>(
      IEnumerable<Expression<Func<TEntity, object?>>> includes, Expression<Func<TEntity, bool>> predicate, 
      CancellationToken cancellationToken, Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy=null);
}