using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Neo.Domain.Features.Concurrency;

namespace Neo.Infrastructure.Features.Crud;

public sealed class NeoConcurrencyInterceptor : SaveChangesInterceptor
{
    private static void Prepare(DbContext? db)
    {
        if (db is null) return;
        db.ChangeTracker.DetectChanges();
        foreach (var entry in db.ChangeTracker.Entries<IConcurrencyVersion>().Where(x => x.State is EntityState.Added or EntityState.Modified))
            entry.Entity.Version = Guid.NewGuid();
    }
    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    { Prepare(data.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Prepare(data.Context); return ValueTask.FromResult(result); }
}

public static class NeoConcurrencyModel
{
    public static ModelBuilder ConfigureNeoConcurrency(this ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes().Where(x => typeof(IConcurrencyVersion).IsAssignableFrom(x.ClrType)))
            model.Entity(entity.ClrType).Property<Guid>(nameof(IConcurrencyVersion.Version)).IsConcurrencyToken().ValueGeneratedNever();
        return model;
    }
}

/// <summary>Reusable SQL Server transaction-scoped update/range lock. Does not emulate locks on other providers.</summary>
public static class SqlServerRowLock
{
    public static async Task AcquireAsync<TEntity>(DbContext db, object key, TimeSpan wait, CancellationToken ct) where TEntity : class
    {
        if (!db.Database.IsSqlServer()) throw new NotSupportedException("Pessimistic row locks require SQL Server.");
        if (wait < TimeSpan.FromSeconds(1) || wait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(wait));
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Begin a database transaction before acquiring a row lock.");
        var entity = db.Model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException("Entity is not mapped.");
        if (entity.BaseType is not null || entity.GetDerivedTypes().Any()) throw new NotSupportedException("Row-lock helper requires a single-table entity without inheritance mapping.");
        var keys = entity.FindPrimaryKey()?.Properties;
        if (keys?.Count != 1) throw new NotSupportedException("Row-lock helper requires a single-column primary key.");
        var table = entity.GetTableName() ?? throw new NotSupportedException("Entity must map to a table.");
        var schema = entity.GetSchema();
        var column = keys[0].GetColumnName(StoreObjectIdentifier.Table(table, schema))!;
        var sql = db.GetService<ISqlGenerationHelper>();
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds);
        command.CommandText = $"SELECT {sql.DelimitIdentifier(column)} FROM {sql.DelimitIdentifier(table, schema)} WITH (UPDLOCK, HOLDLOCK) WHERE {sql.DelimitIdentifier(column)} = @key";
        command.Parameters.Add(keys[0].GetRelationalTypeMapping().CreateParameter(command, "key", key));
        try { await command.ExecuteScalarAsync(ct); }
        catch (SqlException ex) when (ex.Number is -2 or 1205 or 1222)
        { throw new EntityConcurrencyException("resource_busy", "The record is busy; retry the complete operation after reviewing its current version.", ex); }
    }
}
