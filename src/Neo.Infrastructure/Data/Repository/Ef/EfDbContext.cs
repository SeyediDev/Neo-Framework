using Neo.Common.Attributes;
using Neo.Common.Extensions;
using Neo.Common.Utility;
using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Neo.Infrastructure.Data.Repository.Ef;

public abstract partial class EfDbContext<TContext>(DbContextOptions<TContext> options)
    : DbContext(options), IUnitOfWork
    where TContext : DbContext
{
    protected abstract Assembly ContextAssembly { get; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        _ = modelBuilder.ApplyConfigurationsFromAssembly(ContextAssembly);
        modelBuilder.Ignore<BaseEvent>();

        HandelMutableEntityTypes(modelBuilder);
    }

    protected virtual void HandelMutableEntityTypes(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            var entity = modelBuilder.Entity(entityType.ClrType);

            SetEntityTableAndSchema(entityType, entity);

            HandelExpireDateInQuery(entityType!, entity);
        }
    }

    private static void SetEntityTableAndSchema(IMutableEntityType entityType, Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder entity)
    {
        object[] customAttributes = entityType.ClrType.GetTypeInfo().GetCustomAttributes(true);
        var schemaAttribute = GetAttribute<SchemaAttribute>(customAttributes);
        string? schema = schemaAttribute?.Name;
        string entityId = entityType.ClrType.Name;
        if (!entityId.EndsWith("Map"))
            entityId = new PluralNoun().Plural(entityId);
        string tableName = entityId.ToPascalCase(false);
        if (schema == null)
            entity.ToTable(tableName);
        else
            entity.ToTable(tableName, schema);
    }

    protected static T? GetAttribute<T>(IEnumerable<object> attributes) where T : Attribute
    {
        foreach (object attr in attributes)
        {
            if (attr is T attribute)
            {
                return attribute;
            }
        }

        return default!;
    }

    private static void HandelExpireDateInQuery(IMutableEntityType entityType, Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder entity)
    {
        ParameterExpression parameterExpression = Expression.Parameter(entityType.ClrType, "x");
        Expression? filter = null;

        // ExpireDate filter
        var expireProp = entityType.FindProperty(nameof(ISoftDelete.ExpireDate));
        if (expireProp != null && expireProp.ClrType == typeof(DateTime?))
        {
            var property = Expression.Property(parameterExpression, nameof(ISoftDelete.ExpireDate));
            var nullConst = Expression.Constant(null, typeof(DateTime?));
            var expireFilter = Expression.Equal(property, nullConst);
            filter = expireFilter;
        }

        // IsDeleted filter
        var isDeletedProp = entityType.FindProperty(nameof(ISoftDelete.IsDeleted));
        if (isDeletedProp != null && isDeletedProp.ClrType == typeof(bool))
        {
            var property = Expression.Property(parameterExpression, nameof(ISoftDelete.IsDeleted));
            var falseConst = Expression.Constant(false);
            var isDeletedFilter = Expression.Equal(property, falseConst);

            filter = filter == null ? isDeletedFilter : Expression.AndAlso(filter, isDeletedFilter);
        }

        // Apply the combined filter
        if (filter != null)
        {
            var lambda = Expression.Lambda(filter, parameterExpression);
            entity.HasQueryFilter(lambda);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
    }

    public object SetEntity<TEntity>() where TEntity : class, new()
    {
        return Set<TEntity>();
    }

    public object SetEntity(Type entityType)
    {
        MethodInfo? genericSet = null;
        foreach (MethodInfo method in typeof(DbContext).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.Name == nameof(Set) && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
            {
                genericSet = method;
                break;
            }
        }

        if (genericSet == null)
        {
            throw new InvalidOperationException($"Unable to locate generic {nameof(Set)} method on {nameof(DbContext)}.");
        }

        return genericSet.MakeGenericMethod(entityType).Invoke(this, null)!;
    }

    private IDbContextTransaction? _transaction;

    public async Task DoTransaction(Func<Task> body, Func<Exception,Task>? failBack=null)
    {
        await BeginTransactionAsync();
        try
        {
            await body();
        }
        catch(Exception e)
        {
            await RollbackTransactionAsync();
            if(failBack is not null )
            {
                await failBack(e);
            }
        }
        await CommitTransactionAsync();
    }
    public async Task BeginTransactionAsync()
    {
        _transaction = await Database.BeginTransactionAsync();
    }

    public async Task RollbackTransactionAsync()
    {
        if (_transaction == null)
        {
            throw new InvalidOperationException("Please call `BeginTransaction()` method first.");
        }

        await _transaction.RollbackAsync();
    }

    public async Task CommitTransactionAsync()
    {
        if (_transaction == null)
        {
            throw new InvalidOperationException("Please call `BeginTransaction()` method first.");
        }

        await _transaction.CommitAsync();
    }

    // متد برای دریافت نام جدول و اسکیمای یک انتیتی
    public EntityTableInfo? GetEntityTableInfo(Type entity)
    {
        IEntityType? entityType = GetEntityType(entity);
        if (entityType == null)
        {
            return null;
        }
        EntityTableInfo entityTableInfo = new()
        {
            // نام جدول
            TableName = entityType.GetTableName() ?? entityType.GetViewName(),

            // اسکیمای جدول
            Schema = entityType.GetSchema()
        };
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey != null)
        {
            foreach (var property in primaryKey.Properties)
            {
                var info = GetEntityFieldColumnInfo(entity, property.Name);
                if (info != null)
                    entityTableInfo.PrimaryKeys.Add(info);
            }
        }
        var properties = entityType.GetProperties();
        if (properties != null)
        {
            foreach (var property in properties)
            {
                var info = GetEntityFieldColumnInfo(entity, property.Name);
                if (info != null)
                { 
                    entityTableInfo.Properties.Add(info.Id, info); 
                }
            }
        }
        return entityTableInfo;
    }

    // متد برای دریافت اطلاعات فیلد 
    public EntityFieldColumnInfo? GetEntityFieldColumnInfo(Type entity, string propertyName)
    {
        IEntityType? entityType = GetEntityType(entity);
        if (entityType == null)
        {
            return null;
        }

        IProperty? property;
        try
        {
            property = entityType.FindProperty(propertyName);
        }
        catch
        {
            return null;
        }
        EntityFieldColumnInfo info = new() { Id = propertyName };
        try
        {
            info.Name = property?.GetColumnName();
        }
        catch { }
        try
        {
            info.Comment = property?.GetComment();
        }
        catch { }
        try
        {
            info.IsIdentity = property?.ValueGenerated == ValueGenerated.OnAdd;
        }
        catch { }

        return info;
    }

    private IEntityType? GetEntityType(Type entity)
    {
        try
        {
            return Model.FindEntityType(entity);
        }
        catch (Exception exp)
        {
            exp.Source = "GetEntityType";
            return null;
        }
    }
    public void ExecuteSqlInterpolatedCommand(FormattableString query)
    {
        _ = Database.ExecuteSqlInterpolated(query);
    }

    public void ExecuteSqlRawCommand(string query, params object[] parameters)
    {
        _ = Database.ExecuteSqlRaw(query, parameters);
    }



    public async Task ExecuteSqlRawCommandAsync(string query, params object[] parameters)
    {
        _ = await Database.ExecuteSqlRawAsync(query, parameters);
    }

    public async Task<List<TEntity>> ExecuteSqlQueryAsync<TEntity>(string sql, CancellationToken cancellationToken = default) where TEntity : class, new()
    {
        FormattableString query = FormattableStringFactory.Create(sql);
        return await Database.SqlQuery<TEntity>(query).ToListAsync(cancellationToken);
    }

    private bool _isDisposed;

    public sealed override void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            try
            {
                if (disposing)
                {
                    _transaction?.Dispose();
                    _transaction = null;
                }
            }
            finally
            {
                _isDisposed = true;
            }
        }

        base.Dispose();
    }
}

