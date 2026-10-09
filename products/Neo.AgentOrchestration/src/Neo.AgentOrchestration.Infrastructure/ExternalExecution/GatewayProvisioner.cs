using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public static class GatewayProvisioner
{
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
        await db.Database.MigrateAsync(ct);
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("Gateway migration incomplete.");
    }
}
