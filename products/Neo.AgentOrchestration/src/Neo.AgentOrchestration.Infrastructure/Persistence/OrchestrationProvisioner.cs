using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public static class OrchestrationProvisioner
{
    public static void ValidateDestination(string connectionString, string expectedDatabase)
    {
        // This initial provisioner deliberately refuses legacy/system catalogs.
        // A future installer may support an explicitly reviewed naming policy.
        if (!Regex.IsMatch(expectedDatabase, @"\ANeoAgentOrchestration(?:_[A-Za-z0-9_]+)?\z",
            RegexOptions.CultureInvariant) || expectedDatabase.Length > 128)
            throw new ArgumentException("Use NeoAgentOrchestration or a suffixed isolated installation name.");
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.InitialCatalog, expectedDatabase, StringComparison.Ordinal))
            throw new ArgumentException("Connection catalog does not match the explicit destination.");
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
            throw new ArgumentException("Attached database files are not supported by this provisioner.");
    }

    public static async Task MigrateAsync(string connectionString, string expectedDatabase, CancellationToken ct)
    {
        ValidateDestination(connectionString, expectedDatabase);
        await using var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE NOT (s.name='nao' OR (s.name='dbo' AND t.name='__EFMigrationsHistory'));
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("Destination contains unrelated tables; refusing migration.");
        }
        catch (SqlException ex) when (ex.Number == 4060)
        {
            // Missing catalog: EF's explicit Migrate will create it if permitted.
            // Login/permission failures still fail; never fall back to another DB.
        }
        await using var db = new OrchestrationDbContext(new DbContextOptionsBuilder<OrchestrationDbContext>()
            .UseSqlServer(connectionString).Options);
        if (!db.Database.GetMigrations().Any())
            throw new InvalidOperationException("No versioned migrations are included in this build.");
        await db.Database.MigrateAsync(ct);
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("Database migration remains incomplete.");
    }
}
