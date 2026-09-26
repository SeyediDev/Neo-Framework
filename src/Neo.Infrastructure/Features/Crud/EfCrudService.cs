using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo.Application.Exceptions;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Concurrency;

namespace Neo.Infrastructure.Features.Crud;

/// <summary>Participants stage database work after the entity save but before the shared transaction commits. No external calls.</summary>
public interface ICrudTransactionParticipant<TContext,TEntity> where TContext : DbContext where TEntity : class
{
    Task StageAsync(TContext context, CrudOperation operation, TEntity entity, object? input, CancellationToken ct);
}

public sealed class EfCrudService<TContext,TDefinition,TCreate,TUpdate,TRead,TEntity,TKey>(
    TContext db, TDefinition definition, IEnumerable<ICrudTransactionParticipant<TContext,TEntity>> participants)
    : ICrudService<TDefinition,TCreate,TUpdate,TRead,TKey>
    where TContext : DbContext
    where TDefinition : CrudDefinition<TCreate,TUpdate,TRead,TEntity,TKey>
    where TCreate : class where TUpdate : class where TRead : class
    where TEntity : class, IEntity<TKey>, IConcurrencyVersion where TKey : struct
{
    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = definition.ValidateConfiguration().ToList();
        var entity = db.Model.FindEntityType(typeof(TEntity));
        if (entity?.FindProperty(nameof(IConcurrencyVersion.Version))?.IsConcurrencyToken != true)
            errors.Add("Map Version as an EF concurrency token using ConfigureNeoConcurrency.");
        if (entity?.FindPrimaryKey()?.Properties is not { Count: 1 } key || key[0].Name != nameof(IEntity<TKey>.Id))
            errors.Add("CRUD requires Id as the single primary key.");
        if (!db.Database.IsRelational()) errors.Add("Transactional CRUD requires a relational provider.");
        if (definition.Concurrency == EntityConcurrencyMode.Pessimistic && !db.Database.IsSqlServer())
            errors.Add("Pessimistic mode requires SQL Server; no in-memory fallback exists.");
        if (definition.Concurrency == EntityConcurrencyMode.Pessimistic && (entity?.BaseType is not null || entity?.GetDerivedTypes().Any() == true))
            errors.Add("Pessimistic mode requires a single-table entity without inheritance mapping.");
        if (db.Database.CreateExecutionStrategy().RetriesOnFailure) errors.Add("Disable automatic EF retries for this context; retry the complete resource operation explicitly.");
        return errors;
    }
    private void Ensure(CrudOperation operation)
    {
        var errors = ValidateConfiguration();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
        if (!definition.Operations.HasFlag(operation)) throw new NotSupportedException("This resource operation is disabled.");
    }
    private static Expression<Func<TEntity,bool>> HasId(TKey id)
    {
        var row = Expression.Parameter(typeof(TEntity), "row");
        return Expression.Lambda<Func<TEntity,bool>>(Expression.Equal(Expression.Property(row, nameof(IEntity<TKey>.Id)), Expression.Constant(id)), row);
    }
    private CrudItem<TKey,TRead> Item(TEntity entity) => new(entity.Id, entity.Version, definition.Read(entity));
    public async Task<CrudPage<TKey,TRead>> ListAsync(CrudQuery request, CancellationToken ct)
    {
        Ensure(CrudOperation.List);
        if (request.PageNumber < 1 || request.PageSize < 1 || request.PageSize > definition.MaxPageSize || (long)(request.PageNumber - 1) * request.PageSize > int.MaxValue)
            throw new BadRequestException($"PageNumber must be positive and PageSize must be 1..{definition.MaxPageSize}; offset must fit Int32.");
        if (request.Filters is null || request.Filters.Count > 10) throw new BadRequestException("At most ten named filters are allowed.");
        var query = definition.Scope(db.Set<TEntity>().AsNoTracking());
        foreach (var filter in request.Filters)
        {
            if (!definition.Filters.TryGetValue(filter.Key, out var build) || filter.Value is null || filter.Value.Length > 256)
                throw new BadRequestException("Unknown filter or invalid filter value.");
            try { query = query.Where(build(filter.Value)); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            { throw new BadRequestException("Invalid filter value."); }
        }
        IOrderedQueryable<TEntity> ordered;
        if (string.IsNullOrWhiteSpace(request.Sort)) ordered = request.Descending ? query.OrderByDescending(x => x.Id) : query.OrderBy(x => x.Id);
        else if (definition.Sorts.TryGetValue(request.Sort, out var sort)) ordered = sort(query, request.Descending).ThenBy(x => x.Id);
        else throw new BadRequestException("Unknown sort field.");
        var rows = await ordered.Skip(checked((request.PageNumber - 1) * request.PageSize)).Take(request.PageSize + 1).ToListAsync(ct);
        return new(rows.Take(request.PageSize).Select(Item).ToList(), rows.Count > request.PageSize);
    }
    public async Task<CrudItem<TKey,TRead>?> GetAsync(TKey id, CancellationToken ct)
    {
        Ensure(CrudOperation.Read);
        var row = await definition.Scope(db.Set<TEntity>().AsNoTracking()).SingleOrDefaultAsync(HasId(id), ct);
        return row is null ? null : Item(row);
    }
    public Task<CrudItem<TKey,TRead>> CreateAsync(TCreate input, CancellationToken ct)
    {
        Ensure(CrudOperation.Create); definition.ValidateCreate(input);
        return Mutate(CrudOperation.Create, default, input, null, Guid.Empty, ct);
    }
    public Task<CrudItem<TKey,TRead>> UpdateAsync(TKey id, TUpdate input, Guid expectedVersion, CancellationToken ct)
    {
        Ensure(CrudOperation.Update); definition.ValidateUpdate(input);
        return Mutate(CrudOperation.Update, id, null, input, expectedVersion, ct);
    }
    public async Task DeleteAsync(TKey id, Guid expectedVersion, CancellationToken ct)
    { Ensure(CrudOperation.Delete); await Mutate(CrudOperation.Delete, id, null, null, expectedVersion, ct); }

    private async Task<CrudItem<TKey,TRead>> Mutate(CrudOperation operation, TKey id, TCreate? create, TUpdate? update, Guid expected, CancellationToken ct)
    {
        if (operation != CrudOperation.Create && expected == Guid.Empty) throw new BadRequestException("ExpectedVersion is required.");
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Use a fresh scoped DbContext for each CRUD mutation; it owns the transaction.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var previousTimeout = db.Database.GetCommandTimeout();
        if (definition.Concurrency == EntityConcurrencyMode.Pessimistic)
            db.Database.SetCommandTimeout((int)Math.Ceiling(definition.LockWait.TotalSeconds));
        try
        {
            TEntity entity;
            if (operation == CrudOperation.Create)
            {
                entity = definition.Create(create!); entity.Version = Guid.NewGuid(); db.Add(entity);
            }
            else
            {
                if (definition.Concurrency == EntityConcurrencyMode.Pessimistic)
                {
                    if (!await definition.Scope(db.Set<TEntity>().AsNoTracking()).AnyAsync(HasId(id), ct))
                        throw new Ardalis.GuardClauses.NotFoundException(id.ToString()!, definition.Name);
                    await SqlServerRowLock.AcquireAsync<TEntity>(db, id, definition.LockWait, ct);
                }
                entity = await definition.Scope(db.Set<TEntity>()).SingleOrDefaultAsync(HasId(id), ct)
                    ?? throw new Ardalis.GuardClauses.NotFoundException(id.ToString()!, definition.Name);
                if (entity.Version != expected) throw new EntityConcurrencyException("stale_version", "The record changed; reload it before submitting another change.");
                if (operation == CrudOperation.Update)
                {
                    definition.Update(entity, update!);
                    if (!EqualityComparer<TKey>.Default.Equals(entity.Id, id)) throw new InvalidOperationException("Update mapping cannot change the record key.");
                    entity.Version = Guid.NewGuid();
                }
                else { definition.BeforeDelete(entity); db.Remove(entity); }
            }
            await db.SaveChangesAsync(ct);
            foreach (var participant in participants) await participant.StageAsync(db, operation, entity, (object?)create ?? update, ct);
            await db.SaveChangesAsync(ct);
            var result = Item(entity);
            await transaction.CommitAsync(ct);
            db.ChangeTracker.Clear();
            return result;
        }
        catch (Exception error)
        {
            try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await transaction.RollbackAsync(cleanup.Token); }
            catch (Exception cleanupError) { error.Data["CrudRollbackError"] = cleanupError.GetType().Name; }
            db.ChangeTracker.Clear();
            if (error is DbUpdateConcurrencyException) throw new EntityConcurrencyException("stale_version", "The record changed; reload it before submitting another change.", error);
            var sqlError = error as SqlException ?? (error as DbUpdateException)?.InnerException as SqlException;
            if (sqlError?.Number is -2 or 1205 or 1222)
                throw new EntityConcurrencyException("resource_busy", "The record is busy; retry the complete operation after reviewing its current version.", error);
            throw;
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
    }
}
