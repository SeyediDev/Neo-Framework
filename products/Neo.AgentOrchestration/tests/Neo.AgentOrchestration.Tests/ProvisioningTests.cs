using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

[CollectionDefinition("Provisioning", DisableParallelization = true)]
public sealed class ProvisioningCollection;

[Collection("Provisioning")]
public sealed class ProvisioningTests
{
    private const string Database = "NeoAgentOrchestration_Verification";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Init_is_offline_protects_existing_files_and_rejects_legacy_destinations()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "bootstrap.json");
        Assert.Equal(0, (await Cli(null, "--help")).Code);
        Assert.Equal(0, (await Cli(null, "init", path, Database)).Code);
        var original = await File.ReadAllTextAsync(path, Ct);
        Assert.NotNull(JsonSerializer.Deserialize<BootstrapManifest>(original, Json));
        Assert.DoesNotContain("connection", original, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await Cli(null, "init", path, Database)).Code);
        Assert.Equal(original, await File.ReadAllTextAsync(path, Ct));
        var forbidden = Path.Combine(temp.Path, "forbidden.json");
        Assert.Equal(2, (await Cli(null, "init", forbidden, "WorkManagement")).Code);
        Assert.False(File.Exists(forbidden));
        Assert.Equal(2, (await Cli(null, "seed", Database, path)).Code);
        Assert.Equal(2, (await Cli(null, "unexpected-private-marker")).Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("role-collision")]
    [InlineData("large")]
    public async Task Invalid_manifests_fail_before_connecting_and_do_not_echo_secrets(string kind)
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "invalid.json");
        var json = JsonSerializer.Serialize(BootstrapManifest.Template(Database), Json);
        json = kind switch
        {
            "unknown" => json.Insert(1, "\"password\":\"private-marker\","),
            "duplicate" => json.Insert(1, "\"schemaVersion\":1,"),
            "missing" => json.Replace("\"schemaVersion\":1,", "", StringComparison.Ordinal),
            "null" => JsonSerializer.Serialize(BootstrapManifest.Template(Database) with { Roles = null! }, Json),
            "version" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":999", StringComparison.Ordinal),
            "role-collision" => JsonSerializer.Serialize(BootstrapManifest.Template(Database) with
                { Roles = [new("developer", "Developer"), new("DEVELOPER", "Developer")] }, Json),
            _ => new string('x', 65537)
        };
        await File.WriteAllTextAsync(path, json, Ct);
        var result = await Cli($"Server=127.0.0.1,1;Database={Database};User Id=private-marker;Password=private-marker;Connect Timeout=1", "seed", Database, path);
        Assert.Equal(2, result.Code);
        Assert.DoesNotContain("private-marker", result.Output + result.Error);
    }

    [Fact]
    public async Task Cli_migrate_seed_replay_and_health_roundtrip_without_agents_or_dispatch()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "bootstrap.json");
        Assert.Equal(0, (await Cli(null, "init", path, Database)).Code);
        var manifest = UniqueManifest();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, Json), Ct);
        Assert.Equal(0, (await Cli(f.Connection, "migrate", Database)).Code);
        var health = await Cli(f.Connection, "health", Database);
        Assert.Equal(0, health.Code);
        Assert.True(JsonSerializer.Deserialize<DatabaseHealth>(health.Output, Json)!.Ready);
        var first = await Cli(f.Connection, "seed", Database, path);
        Assert.Equal(0, first.Code);
        var seeded = JsonSerializer.Deserialize<BootstrapResult>(first.Output, Json)!;
        Assert.Equal(5, seeded.CreatedRecords);
        var replay = await Cli(f.Connection, "seed", Database, path);
        Assert.Equal(0, replay.Code);
        var again = JsonSerializer.Deserialize<BootstrapResult>(replay.Output, Json)!;
        Assert.Equal(0, again.CreatedRecords);
        Assert.Equal(seeded.WorkspaceId, again.WorkspaceId);
        Assert.Equal(seeded.OrganizationId, again.OrganizationId);
        Assert.Equal(seeded.ProjectId, again.ProjectId);
        Assert.Equal(seeded.RoleIds.OrderBy(x => x.Key), again.RoleIds.OrderBy(x => x.Key));
        await using var read = f.Factory.CreateDbContext();
        Assert.Equal(2, await read.Roles.CountAsync(x => x.WorkspaceId == seeded.WorkspaceId, Ct));
        Assert.True(await read.Projects.AnyAsync(x => x.Id == seeded.ProjectId && x.WorkspaceId == seeded.WorkspaceId && x.OrganizationId == seeded.OrganizationId, Ct));
        Assert.False(await read.Agents.AnyAsync(x => x.WorkspaceId == seeded.WorkspaceId, Ct));
        Assert.False(await read.Workflows.AnyAsync(x => x.WorkspaceId == seeded.WorkspaceId, Ct));
        Assert.False(await read.WorkItems.AnyAsync(x => x.WorkspaceId == seeded.WorkspaceId, Ct));
        Assert.False(await read.AgentRuns.AnyAsync(x => x.WorkspaceId == seeded.WorkspaceId, Ct));
        var catalog = await new WorkspaceHandlers(f.Store, f.Clock).Handle(
            new GetWorkspaceCatalog(new WorkspaceScope(seeded.OrganizationId, seeded.WorkspaceId)), Ct);
        Assert.Equal(seeded.ProjectId, Assert.Single(catalog.Projects).Id);
        Assert.Equal(2, catalog.Roles.Count);
        var invalid = manifest with { Database = "WorkManagement" };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(invalid, Json), Ct);
        Assert.Equal(2, (await Cli(f.Connection, "seed", Database, path)).Code);
        Assert.Equal(2, (await Cli(f.Connection, "health", "WorkManagement")).Code);
    }

    [Fact]
    public async Task Concurrent_seed_is_idempotent_and_conflicts_roll_back_without_overwriting()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var manifest = UniqueManifest();
        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => OrchestrationBootstrap.SeedAsync(f.Connection, Database, manifest, Ct)));
        Assert.Single(results.Select(x => x.WorkspaceId).Distinct());
        Assert.Equal(5, results.Sum(x => x.CreatedRecords));
        var seed = results[0];
        // Stage a new project and role before encountering a conflicting existing role.
        var conflict = manifest with { Project = new("extra", "Extra"), Roles = [
            new("extra", "Extra"), manifest.Roles[0] with { Name = "Changed" }] };
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "conflict.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(conflict, Json), Ct);
        Assert.Equal(4, (await Cli(f.Connection, "seed", Database, path)).Code);
        await using (var read = f.Factory.CreateDbContext())
        {
            Assert.False(await read.Projects.AnyAsync(x => x.WorkspaceId == seed.WorkspaceId && x.Key == "EXTRA", Ct));
            Assert.False(await read.Roles.AnyAsync(x => x.WorkspaceId == seed.WorkspaceId && x.Key == "EXTRA", Ct));
            var role = await read.Roles.SingleAsync(x => x.Id == seed.RoleIds["DEVELOPER"], Ct);
            Assert.Equal(manifest.Roles[0].Name, role.Name);
            role.SetEnabled(new(seed.OrganizationId, seed.WorkspaceId), false);
            await read.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<BootstrapConflictException>(() => OrchestrationBootstrap.SeedAsync(f.Connection, Database, manifest, Ct));
        await using var verify = f.Factory.CreateDbContext();
        Assert.False((await verify.Roles.SingleAsync(x => x.Id == seed.RoleIds["DEVELOPER"], Ct)).IsEnabled);
    }

    private static BootstrapManifest UniqueManifest() => BootstrapManifest.Template(Database) with
    { Organization = new(Guid.NewGuid().ToString("N"), "CLI verification") };

    [Fact]
    public async Task Health_of_missing_catalog_is_not_ready_and_does_not_create_it()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var missing = "NeoAgentOrchestration_Health_" + Guid.NewGuid().ToString("N");
        var connection = new SqlConnectionStringBuilder(f.Connection) { InitialCatalog = missing }.ConnectionString;
        Assert.Equal(3, (await Cli(connection, "health", missing)).Code);
        await using var sql = new SqlConnection(f.Connection);
        await sql.OpenAsync(Ct);
        await using var command = sql.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name=@name";
        command.Parameters.AddWithValue("@name", missing);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync(Ct)));
    }

    private static async Task<(int Code, string Output, string Error)> Cli(string? connection, params string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Neo.AgentOrchestration.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = typeof(ProvisioningTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var dll = Path.Combine(root.FullName, "src", "Neo.AgentOrchestration.Provisioning", "bin", configuration, "net10.0", "neo-agent.dll");
        Assert.True(File.Exists(dll));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        if (connection is not null) start.Environment["NEO_ORCHESTRATION_SQL"] = connection;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(90), Ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neo-cli-test-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true); // Only this test's freshly-created temporary directory.
    }
}
