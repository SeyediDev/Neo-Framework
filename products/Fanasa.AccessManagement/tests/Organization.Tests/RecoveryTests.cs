using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

static class RecoveryTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        void Reject<T>(Action action, string label) where T : Exception { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        var root = Path.Combine(directory, "recovery"); Directory.CreateDirectory(root);
        var fabric = Path.Combine(root, "source-fabric.db"); var registryPath = Path.Combine(root, "source-registry.db");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = registryPath }).Build();
        var registry = new PlatformRegistry(settings); using var database = new FabricDatabase(fabric);
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(registry)); var tenant = access.GetTenants().Single().Id;
        access.AddUser(new(tenant, "recovery-member", "Member")); access.SetGrant(new(tenant, "recovery-member", "organization.read", null), "test", "test");
        registry.SetTenantSource(access.GetTenants); registry.SetMembershipSource(access.GetUsers);
        registry.Grant(new("recovery-member", tenant, "developer.read", null), "test");
        var chart = new OrganizationStore(root, access, database: database);
        chart.Execute(tenant, new(0, "unit.save", null, "Unit", "root", null, "team", null, 1, null, null, null, null, "recovery-test"), "test");
        var billing = new AccountingStore(root, access, database: database);
        billing.Execute(tenant, new(0, "credit", "credit", null, null, 0, null, null, 0, 0, 1000, "test", null, null), "test");
        using var walConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fabric, Pooling = false }.ToString()); walConnection.Open();
        database.Transaction(() => { database.Put("recovery-test", "wal", "committed"); return true; });
        var bundle = Path.Combine(root, "bundle");
        Reject<ArgumentException>(() => FabricRecovery.Backup(fabric, registryPath, bundle, false), "backup requires explicit offline confirmation");
        var manifest = FabricRecovery.Backup(fabric, registryPath, bundle, true);
        check(manifest.Files.Length == 2 && FabricRecovery.Verify(bundle).Format == 1, "recovery bundle verifies both databases with checksums and integrity");
        Reject<IOException>(() => FabricRecovery.Backup(fabric, registryPath, bundle, true), "backup cannot overwrite an existing bundle");
        var restored = Path.Combine(root, "restored"); FabricRecovery.Restore(bundle, restored, true);
        using var recoveredDb = new FabricDatabase(Path.Combine(restored, "fabric.db"));
        settings["Platform:RegistryPath"] = Path.Combine(restored, "platform-registry.db"); var recoveredRegistry = new PlatformRegistry(settings);
        var recoveredAccess = new PersistentAccessManagement(recoveredDb, new InMemoryAccessManagement(recoveredRegistry));
        recoveredRegistry.SetTenantSource(recoveredAccess.GetTenants); recoveredRegistry.SetMembershipSource(recoveredAccess.GetUsers);
        check(recoveredAccess.GetUsers(tenant).Single().KeycloakSubject == "recovery-member" && recoveredRegistry.Allows("recovery-member", tenant, "developer.read"), "restore preserves memberships and central registry grants together");
        var recoveredChart = new OrganizationStore(root, recoveredAccess, database: recoveredDb);
        check(recoveredChart.Read(tenant).Revision == 1 && recoveredChart.History(tenant, 1).Units.Single().Code == "root", "restore preserves current chart and historical snapshot");
        check(new AccountingStore(root, recoveredAccess, database: recoveredDb).Read(tenant).WalletBalance == 1000 && recoveredDb.Outbox().Length == database.Outbox().Length, "restore preserves ledger and undelivered outbox");
        check(recoveredDb.Read<string>("recovery-test", "wal") == "committed", "SQLite backup includes committed WAL data");
        Reject<IOException>(() => FabricRecovery.Restore(bundle, restored, true), "restore cannot overwrite active data directory");
        Reject<IOException>(() => FabricRecovery.Restore(bundle, Path.Combine(bundle, "nested"), true), "restore cannot contaminate its source backup bundle");
        check(FabricRecovery.Verify(bundle).Files.Length == 2, "rejected nested restore preserves source bundle");
        var tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
        foreach (var name in new[] { "fabric.db", "platform-registry.db", "manifest.json" }) File.Copy(Path.Combine(bundle, name), Path.Combine(tampered, name));
        using (var stream = new FileStream(Path.Combine(tampered, "fabric.db"), FileMode.Open, FileAccess.Write)) { stream.Position = 100; stream.WriteByte(99); }
        var rejectedDestination = Path.Combine(root, "rejected");
        Reject<InvalidDataException>(() => FabricRecovery.Restore(tampered, rejectedDestination, true), "restore rejects corrupted backup before publishing destination");
        check(!Directory.Exists(rejectedDestination), "failed verification leaves destination absent");
        var escaped = manifest with { Files = [manifest.Files[0] with { Name = "../fabric.db" }, manifest.Files[1]] };
        File.WriteAllText(Path.Combine(tampered, "manifest.json"), JsonSerializer.Serialize(escaped));
        Reject<InvalidDataException>(() => FabricRecovery.Verify(tampered), "manifest path traversal rejected");
        File.WriteAllText(Path.Combine(tampered, "manifest.json"), "{\"Format\":1,\"Files\":[null,null]}");
        Reject<InvalidDataException>(() => FabricRecovery.Verify(tampered), "null manifest entries rejected with controlled error");
        var wrong = Path.Combine(root, "wrong.db"); using (var connection = new SqliteConnection("Data Source=" + wrong)) { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE unrelated(id INTEGER);"; command.ExecuteNonQuery(); }
        Reject<SqliteException>(() => FabricRecovery.Backup(wrong, registryPath, Path.Combine(root, "wrong-bundle"), true), "backup refuses unrelated SQLite schema");
    }
}
