using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Concurrency;

namespace Neo.Application.Features.Crud;

[Flags]
public enum CrudOperation { List = 1, Read = 2, Create = 4, Update = 8, Delete = 16, All = 31 }

public sealed record CrudItem<TKey,TRead>(TKey Id, Guid Version, TRead Data);
public sealed record CrudPage<TKey,TRead>(IReadOnlyList<CrudItem<TKey,TRead>> Items, bool HasNext);
public sealed record CrudUpdate<TUpdate>(TUpdate Data, Guid ExpectedVersion);
public sealed class CrudQuery
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Sort { get; set; }
    public bool Descending { get; set; }
    public Dictionary<string,string> Filters { get; set; } = [];
}

public interface ICrudDefinition
{
    string Name { get; }
    CrudOperation Operations { get; }
    IReadOnlyDictionary<CrudOperation,string?> Policies { get; }
    EntityConcurrencyMode Concurrency { get; }
    int MaxPageSize { get; }
    TimeSpan LockWait { get; }
    IReadOnlyList<string> ValidateConfiguration();
}

/// <summary>Scoped application policy and mapping. Null policy explicitly permits anonymous access; omitted policies are invalid.</summary>
public abstract class CrudDefinition<TCreate,TUpdate,TRead,TEntity,TKey> : ICrudDefinition
    where TCreate : class where TUpdate : class where TRead : class
    where TEntity : class, IEntity<TKey>, IConcurrencyVersion where TKey : struct
{
    public abstract string Name { get; }
    public virtual CrudOperation Operations => CrudOperation.All;
    public abstract IReadOnlyDictionary<CrudOperation,string?> Policies { get; }
    public virtual EntityConcurrencyMode Concurrency => EntityConcurrencyMode.Optimistic;
    public virtual int MaxPageSize => 200;
    public virtual TimeSpan LockWait => TimeSpan.FromSeconds(3);
    public virtual IReadOnlyDictionary<string,Func<string,Expression<Func<TEntity,bool>>>> Filters =>
        new Dictionary<string,Func<string,Expression<Func<TEntity,bool>>>>();
    public virtual IReadOnlyDictionary<string,Func<IQueryable<TEntity>,bool,IOrderedQueryable<TEntity>>> Sorts =>
        new Dictionary<string,Func<IQueryable<TEntity>,bool,IOrderedQueryable<TEntity>>>();
    // A tenant/ownership predicate belongs here and applies to reads, updates and deletes alike.
    public virtual IQueryable<TEntity> Scope(IQueryable<TEntity> query) => query;
    public abstract TEntity Create(TCreate input);
    public abstract void Update(TEntity entity, TUpdate input);
    public abstract TRead Read(TEntity entity);
    public virtual void BeforeDelete(TEntity entity) { }
    public virtual void ValidateCreate(TCreate input) => ValidateInput(input);
    public virtual void ValidateUpdate(TUpdate input) => ValidateInput(input);
    private static void ValidateInput(object input)
    {
        if (input is null) throw new BadRequestException("Input is required.");
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), results, true))
            throw new BadRequestException(string.Join(" ", results.Select(x => x.ErrorMessage)));
    }
    public virtual IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Resource name is required.");
        if ((Operations & ~CrudOperation.All) != 0) errors.Add("Unknown CRUD operation.");
        if (!Enum.IsDefined(Concurrency)) errors.Add("Unknown concurrency mode.");
        if (MaxPageSize is < 1 or > 200) errors.Add("MaxPageSize must be 1..200.");
        if (LockWait < TimeSpan.FromSeconds(1) || LockWait > TimeSpan.FromSeconds(30)) errors.Add("LockWait must be 1..30 seconds.");
        foreach (var op in new[] { CrudOperation.List, CrudOperation.Read, CrudOperation.Create, CrudOperation.Update, CrudOperation.Delete })
            if (Operations.HasFlag(op) && (!Policies.TryGetValue(op, out var policy) || policy is not null && string.IsNullOrWhiteSpace(policy)))
                errors.Add($"Declare a policy for {op}; null explicitly permits anonymous access.");
        return errors;
    }
}

public interface ICrudService<TCreate,TUpdate,TRead,TKey>
    where TCreate : class where TUpdate : class where TRead : class where TKey : struct
{
    Task<CrudPage<TKey,TRead>> ListAsync(CrudQuery query, CancellationToken ct);
    Task<CrudItem<TKey,TRead>?> GetAsync(TKey id, CancellationToken ct);
    Task<CrudItem<TKey,TRead>> CreateAsync(TCreate input, CancellationToken ct);
    Task<CrudItem<TKey,TRead>> UpdateAsync(TKey id, TUpdate input, Guid expectedVersion, CancellationToken ct);
    Task DeleteAsync(TKey id, Guid expectedVersion, CancellationToken ct);
}

public interface ICrudService<TDefinition,TCreate,TUpdate,TRead,TKey> : ICrudService<TCreate,TUpdate,TRead,TKey>
    where TDefinition : ICrudDefinition where TCreate : class where TUpdate : class where TRead : class where TKey : struct;
