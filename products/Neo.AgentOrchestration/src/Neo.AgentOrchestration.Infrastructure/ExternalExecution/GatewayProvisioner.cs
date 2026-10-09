using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public static class GatewayProvisioner
{
    // Read-only readiness. Never creates a database, seeds/imports a reservation
    // or invokes native agents. Scope/credentials remain operator supplied.
    public static async Task<GatewayDatabaseHealth> HealthAsync(string connection, string database, CancellationToken ct)
    {
        ValidateDestination(connection, database);
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlServer(connection).Options);
        var expected = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if (!await db.Database.CanConnectAsync(ct))
            return new(false, "connection-unavailable", 0, expected.Count);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0 || !expected.SetEquals(applied))
            return new(false, "migration-mismatch", applied.Count, expected.Count);
        var tables = db.Model.GetEntityTypes().Select(x => $"{x.GetSchema() ?? "dbo"}.{x.GetTableName()}")
            .ToHashSet(StringComparer.Ordinal);
        tables.Add("dbo.__EFMigrationsHistory");
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT s.name+'.'+t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id";
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
        var ready = tables.SetEquals(actual) && !db.Database.HasPendingModelChanges();
        return new(ready, ready ? "schema-current" : "schema-mismatch", applied.Count, expected.Count);
    }

    public static void ValidateDestination(string connection, string database)
    {
        if (database.Length > 128 || !Regex.IsMatch(database, @"\AFanasaAgentGateway(?:_[A-Za-z0-9_]+)?\z"))
            throw new ArgumentException("Use the independent FanasaAgentGateway catalog or an isolated suffix.");
        var c = new SqlConnectionStringBuilder(connection);
        if (c.InitialCatalog != database || !string.IsNullOrEmpty(c.AttachDBFilename))
            throw new ArgumentException("Gateway catalog must match the explicit destination.");
    }
    // Explicit operator/tool operation, never a startup callback. Preserves
    // existing rows and refuses catalogs containing unrelated tables.
    public static async Task MigrateAsync(string connection, string database, CancellationToken ct)
    {
        ValidateDestination(connection, database);
        await using var sql = new SqlConnection(connection);
        try
        {
            await sql.OpenAsync(ct);
            await using var command = sql.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE NOT(s.name='gateway' OR (s.name='dbo' AND t.name='__EFMigrationsHistory'));
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("Gateway destination contains unrelated tables.");
        }
        catch (SqlException ex) when (ex.Number == 4060) { }
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlServer(connection).Options);
        if (!db.Database.GetMigrations().Any()) throw new InvalidOperationException("Gateway migrations unavailable.");
        if (await db.Database.CanConnectAsync(ct))
        {
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if ((await db.Database.GetAppliedMigrationsAsync(ct)).Any(x => !known.Contains(x)))
                throw new InvalidOperationException("Gateway destination has unknown migrations; reconcile before upgrading.");
        }
        await db.Database.MigrateAsync(ct);
        if (!(await HealthAsync(connection, database, ct)).Ready)
            throw new InvalidOperationException("Gateway schema requires reconciliation after migration.");
    }
}

public sealed record GatewayDatabaseHealth(bool Ready, string Reason, int AppliedMigrations, int ExpectedMigrations);
