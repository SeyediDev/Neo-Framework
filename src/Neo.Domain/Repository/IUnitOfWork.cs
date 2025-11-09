namespace Neo.Domain.Repository;
public interface IUnitOfWork
{
    object SetEntity<TEntity>() where TEntity : class, new();
    object SetEntity(Type entity);
    void ExecuteSqlInterpolatedCommand(FormattableString query);
    void ExecuteSqlRawCommand(string query, params object[] parameters);
    Task ExecuteSqlRawCommandAsync(string query, params object[] parameters);
    Task DoTransaction(Func<Task> body, Func<Exception, Task>? failBack = null);
    Task BeginTransactionAsync();
    Task RollbackTransactionAsync();
    Task CommitTransactionAsync();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    EntityTableInfo? GetEntityTableInfo(Type entity);
    EntityFieldColumnInfo? GetEntityFieldColumnInfo(Type entity, string propertyName);
    Task<List<TEntity>> ExecuteSqlQueryAsync<TEntity>(string sql, CancellationToken cancellationToken = default) where TEntity : class, new();
}