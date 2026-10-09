using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class SqlGatewayJournalTests
{
    [Theory]
    [InlineData("gateway-health")]
    [InlineData("gateway-migrate")]
    public async Task Gateway_cli_requires_its_own_environment_without_falling_back_to_product_sql(string command)
    {
        var result = await GatewayCli(null, "Server=private-marker;Database=NeoAgentOrchestration", command, "FanasaAgentGateway");
        Assert.Equal(2, result.Code);
        Assert.Contains("Missing FANASA_AGENT_GATEWAY_SQL", result.Error);
        Assert.DoesNotContain("private-marker", result.Output + result.Error);
        Assert.Equal(2, (await GatewayCli(null, null, command, "FanasaAgentGateway", "private-marker")).Code);
    }

    [Theory]
    [InlineData("gateway-health", "WorkManagement", "WorkManagement", "")]
    [InlineData("gateway-migrate", "NeoAgentOrchestration", "NeoAgentOrchestration", "")]
    [InlineData("gateway-health", "FanasaAgentGateway_Verification", "FanasaAgentGateway_Other", "")]
    [InlineData("gateway-migrate", "FanasaAgentGateway_Verification", "FanasaAgentGateway_Verification", ";AttachDBFilename=private-marker")]
    public async Task Gateway_cli_rejects_legacy_mismatched_or_attached_catalog_before_connecting(
        string command, string destination, string connectionCatalog, string extra)
    {
        var result = await GatewayCli($"Server=127.0.0.1,1;Database={connectionCatalog};User Id=private-marker;Password=private-marker;Connect Timeout=1{extra}",
            null, command, destination);
        Assert.Equal(2, result.Code);
        Assert.DoesNotContain("private-marker", result.Output + result.Error);
    }

    [Fact]
    public async Task Gateway_cli_unreachable_catalog_is_not_ready_and_does_not_echo_connection()
    {
        var result = await GatewayCli("Server=127.0.0.1,1;Database=FanasaAgentGateway_TestOffline;User Id=private-marker;Password=private-marker;Connect Timeout=1",
            null, "gateway-health", "FanasaAgentGateway_TestOffline");
        Assert.Equal(1, result.Code);
        var health = System.Text.Json.JsonSerializer.Deserialize<GatewayDatabaseHealth>(result.Output, Json)!;
        Assert.False(health.Ready); Assert.Equal("connection-unavailable", health.Reason);
        Assert.DoesNotContain("private-marker", result.Output + result.Error);
    }

    [Fact]
    public async Task Gateway_cli_migration_replay_and_read_only_health_preserve_reserved_runs_and_outbox()
    {
        var f = await Fixture.Create();
        var reserved = await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        await using var db = f.Factory.CreateDbContext();
        var connection = db.Database.GetConnectionString()!;
        Assert.Equal(0, (await GatewayCli(connection, null, "gateway-migrate", "FanasaAgentGateway_Verification")).Code);
        Assert.Equal(0, (await GatewayCli(connection, null, "gateway-migrate", "FanasaAgentGateway_Verification")).Code);
        var result = await GatewayCli(connection, null, "gateway-health", "FanasaAgentGateway_Verification");
        Assert.Equal(0, result.Code);
        var health = System.Text.Json.JsonSerializer.Deserialize<GatewayDatabaseHealth>(result.Output, Json)!;
        Assert.True(health.Ready); Assert.Equal("schema-current", health.Reason);
        Assert.Equal(2, health.ExpectedMigrations); Assert.Equal(2, health.AppliedMigrations);
        Assert.Equal(reserved.Version, (await f.Journal.ReadAsync(f.Scope, Ct)).Version);
        Assert.Single(await db.Runs.ToArrayAsync(Ct)); Assert.Single(await db.Activations.ToArrayAsync(Ct));
        Assert.Single(await db.OutboxMessages.ToArrayAsync(Ct)); Assert.Empty(await db.Usage.ToArrayAsync(Ct));
        Assert.Equal(0, f.Adapter.Prepared); Assert.Empty(f.Delivery.Bodies);
    }

    [Fact]
    public async Task Gateway_health_of_missing_catalog_never_creates_it()
    {
        var f = await Fixture.Create(); await using var db = f.Factory.CreateDbContext();
        var catalog = "FanasaAgentGateway_Health_" + Guid.NewGuid().ToString("N");
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString()) { InitialCatalog = catalog }.ConnectionString;
        var result = await GatewayCli(connection, null, "gateway-health", catalog);
        Assert.Equal(1, result.Code);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<GatewayDatabaseHealth>(result.Output, Json)!.Ready);
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name=@name";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = catalog; command.Parameters.Add(parameter);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(Ct)));
    }

    [Fact]
    public async Task Gateway_health_and_migration_refuse_unknown_history_without_removing_it()
    {
        var f = await Fixture.Create(); await using var db = f.Factory.CreateDbContext();
        var connection = db.Database.GetConnectionString()!;
        const string migration = "99999999999999_FanasaGatewayHealthTestFuture";
        await db.Database.ExecuteSqlRawAsync("INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99999999999999_FanasaGatewayHealthTestFuture','10.0.0')", Ct);
        try
        {
            var health = await GatewayProvisioner.HealthAsync(connection, "FanasaAgentGateway_Verification", Ct);
            Assert.False(health.Ready); Assert.Equal("migration-mismatch", health.Reason);
            Assert.Equal(3, (await GatewayCli(connection, null, "gateway-health", "FanasaAgentGateway_Verification")).Code);
            Assert.Equal(1, (await GatewayCli(connection, null, "gateway-migrate", "FanasaAgentGateway_Verification")).Code);
            Assert.Contains(migration, await db.Database.GetAppliedMigrationsAsync(Ct));
        }
        finally
        {
            // Only this case's inserted history row in the disposable fixture.
            await db.Database.ExecuteSqlRawAsync("DELETE dbo.__EFMigrationsHistory WHERE MigrationId='99999999999999_FanasaGatewayHealthTestFuture'", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Gateway_health_detects_unrelated_or_missing_table_and_migration_does_not_repair_silently()
    {
        var f = await Fixture.Create(); await using var db = f.Factory.CreateDbContext();
        var connection = db.Database.GetConnectionString()!;
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.FanasaGatewayHealthTest (Id int NOT NULL)", Ct);
        try
        {
            Assert.Equal("schema-mismatch", (await GatewayProvisioner.HealthAsync(connection, "FanasaAgentGateway_Verification", Ct)).Reason);
            Assert.Equal(3, (await GatewayCli(connection, null, "gateway-health", "FanasaAgentGateway_Verification")).Code);
            Assert.Equal(1, (await GatewayCli(connection, null, "gateway-migrate", "FanasaAgentGateway_Verification")).Code);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.FanasaGatewayHealthTest", CancellationToken.None); }
        await db.Database.ExecuteSqlRawAsync("EXEC sp_rename N'gateway.Usage', N'Usage_HealthTest'", Ct);
        try
        {
            Assert.Equal("schema-mismatch", (await GatewayProvisioner.HealthAsync(connection, "FanasaAgentGateway_Verification", Ct)).Reason);
            Assert.Equal(3, (await GatewayCli(connection, null, "gateway-health", "FanasaAgentGateway_Verification")).Code);
            Assert.Equal(1, (await GatewayCli(connection, null, "gateway-migrate", "FanasaAgentGateway_Verification")).Code);
        }
        finally { await db.Database.ExecuteSqlRawAsync("EXEC sp_rename N'gateway.Usage_HealthTest', N'Usage'", CancellationToken.None); }
        Assert.True((await GatewayProvisioner.HealthAsync(connection, "FanasaAgentGateway_Verification", Ct)).Ready);
    }

    private static async Task<(int Code, string Output, string Error)> GatewayCli(
        string? gatewayConnection, string? productConnection, params string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Neo.AgentOrchestration.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = typeof(SqlGatewayJournalTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var dll = Path.Combine(directory.FullName, "src", "Neo.AgentOrchestration.Provisioning", "bin", configuration, "net10.0", "neo-agent.dll");
        Assert.True(File.Exists(dll));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        if (gatewayConnection is not null) start.Environment["FANASA_AGENT_GATEWAY_SQL"] = gatewayConnection;
        if (productConnection is not null) start.Environment["NEO_ORCHESTRATION_SQL"] = productConnection;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct); var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(90), Ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
