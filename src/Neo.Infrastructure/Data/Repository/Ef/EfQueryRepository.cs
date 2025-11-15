using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Mapster;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Neo.Infrastructure.Data.Repository.Ef;
public abstract class EfQueryRepository<TEntity, TKey, TQueryUnitOfWork>(IUnitOfWork uow)
    : EfRepositoryBase<TEntity, TKey>(uow), IQueryRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
    where TQueryUnitOfWork : IUnitOfWork
{
    public async Task<IEnumerable<TEntity>> GetAllAsync(CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        if (skip != null)
            query = query.Skip(skip.Value);
        if (take != null)
            query = query.Take(take.Value);
        return await query.ToListAsync(cancellationToken);
    }

    public IQueryable<TEntity> GetEntityAsQueryable()
    {
        IQueryable<TEntity> query = _dbSet;
        return query;
    }

    public async Task<IEnumerable<TEntity>> GetAllWithIncludeAsync<TProperty>(
        Expression<Func<TEntity, TProperty>> include, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        if (skip != null)
            query = query.Skip(skip.Value);
        if (take != null)
            query = query.Take(take.Value);
        query = query.Include(include);
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<TEntity>> GetAllWithIncludeAsync(
        IEnumerable<Expression<Func<TEntity, object?>>> includes, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        int? skip = null, int? take = null)
    {
        IQueryable<TEntity> query = _dbSet;
        query = ApplyIncludes(query, includes?.Cast<LambdaExpression>());
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        if (skip != null)
            query = query.Skip(skip.Value);
        if (take != null)
            query = query.Take(take.Value);
        return await query.ToListAsync(cancellationToken);
    }

    public async Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedAsync(
        int pageNumber, int pageSize, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize + 1);
        bool hastNext = false;
        List<TEntity> entities = await query.ToListAsync(cancellationToken);
        if (entities?.Count > pageSize)
        {
            hastNext = true;
            entities.Remove(entities.Last());
        }

        return (entities!, hastNext);
    }

    public async Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedWithIncludeAsync<TProperty>(
        Expression<Func<TEntity, TProperty>> include,
        int pageNumber, int pageSize, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        query = query.Include(include);
        query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize + 1);
        bool hastNext = false;
        List<TEntity> entities = await query.ToListAsync(cancellationToken);
        if (entities?.Count > pageSize)
        {
            hastNext = true;
            entities.Remove(entities.Last());
        }

        return (entities!, hastNext);
    }

    public async Task<(IEnumerable<TEntity> Entities, bool HasNext)> GetPagedWithIncludeAsync<TProperty>(
       IEnumerable<Expression<Func<TEntity, TProperty>>> includes,
       int pageNumber, int pageSize, CancellationToken cancellationToken,
       Expression<Func<TEntity, bool>>? predicate = null,
       Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        query = ApplyIncludes(query, includes?.Cast<LambdaExpression>());
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize + 1);
        bool hastNext = false;
        List<TEntity> entities = await query.ToListAsync(cancellationToken);
        if (entities?.Count > pageSize)
        {
            hastNext = true;
            entities.Remove(entities.Last());
        }

        return (entities!, hastNext);
    }

    public async Task<List<TDto>> GetAllAsync<TDto>(CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        where TDto : class, new()
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        return await query.ProjectToType<TDto>().ToListAsync(cancellationToken);
    }

    public async Task<List<TDto>> GetAllAsync<TDto>(int pageNumber, int pageSize, CancellationToken cancellationToken,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        where TDto : class, new()
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
            query = query.Where(predicate);
        if (orderBy != null)
            query = orderBy(query);
        query = query.Skip((pageNumber - 1) * pageSize).Take(pageSize);
        return await query.ProjectToType<TDto>().ToListAsync(cancellationToken);
    }

    public async Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.Id != null && x.Id.Equals(id), cancellationToken);
    }
    public async Task<TDto?> GetByIdAsync<TDto>(TKey id, CancellationToken cancellationToken)
    {
        return await _dbSet.Where(x => x.Id != null && x.Id.Equals(id)).ProjectToType<TDto>().FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<TEntity?> GetByIdWithIncludeAsync<TProperty>(TKey id,
        Expression<Func<TEntity, TProperty>> include, CancellationToken cancellationToken)
    {
        return await _dbSet
                .Include(include)
                .FirstOrDefaultAsync(e => e.Id !=null && e.Id.Equals(id), cancellationToken);
    }

    public async Task<bool> AnyAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken)
    {
        return await _dbSet.AnyAsync(predicate, cancellationToken);
    }

    public async Task<TEntity?> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken, 
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (orderBy != null)
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
      IEnumerable<Expression<Func<TEntity, object?>>> includes, Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken,
      Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
    {
        IQueryable<TEntity> query = _dbSet;
        query = ApplyIncludes(query, includes?.Cast<LambdaExpression>());
        if (orderBy != null)
            query = orderBy(query);
        return await query.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    private static IQueryable<TEntity> ApplyIncludes(IQueryable<TEntity> query,
        IEnumerable<LambdaExpression>? includes)
    {
        if (includes == null)
        {
            return query;
        }

        foreach (LambdaExpression include in includes)
        {
            if (include == null)
            {
                continue;
            }

            if (!TryExtractIncludePath(include.Body, out string? includePath) || string.IsNullOrWhiteSpace(includePath))
            {
                throw new InvalidOperationException("Include expression must be a simple property access.");
            }

            query = query.Include(includePath);
        }

        return query;
    }

    private static bool TryExtractIncludePath(Expression expression, out string? path)
    {
        path = null;
        Expression? current = expression;

        if (current is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            current = unary.Operand;
        }

        List<string> members = [];
        while (current is MemberExpression member)
        {
            members.Add(member.Member.Name);
            current = member.Expression;

            if (current is UnaryExpression nestedUnary && nestedUnary.NodeType == ExpressionType.Convert)
            {
                current = nestedUnary.Operand;
            }
        }

        if (current is ParameterExpression && members.Count > 0)
        {
            members.Reverse();
            path = string.Join(".", members);
            return true;
        }

        return false;
    }
}

