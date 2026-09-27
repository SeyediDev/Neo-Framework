using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public static class OrchestrationBootstrap
{
    private static OrchestrationDbContext Open(string connection) => new(
        new DbContextOptionsBuilder<OrchestrationDbContext>().UseSqlServer(connection).Options);

    // Read-only: never migrate, create a catalog, seed data or start a worker.
    public static async Task<DatabaseHealth> HealthAsync(string connection, string database, CancellationToken ct)
    {
        OrchestrationProvisioner.ValidateDestination(connection, database);
        await using var db = Open(connection);
        var expected = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0 || !expected.SetEquals(applied))
            return new(false, "migration-mismatch", applied.Count, expected.Count);
        var tables = db.Model.GetEntityTypes().Select(x => $"{x.GetSchema()}.{x.GetTableName()}")
            .ToHashSet(StringComparer.Ordinal);
        tables.Add("dbo.__EFMigrationsHistory");
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT s.name+'.'+t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id";
        var actual = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
        var ready = tables.SetEquals(actual) && !db.Database.HasPendingModelChanges();
        return new(ready, ready ? "schema-current" : "schema-mismatch", applied.Count, expected.Count);
    }

    public static async Task<BootstrapResult> SeedAsync(string connection, string database,
        BootstrapManifest manifest, CancellationToken ct)
    {
        OrchestrationProvisioner.ValidateDestination(connection, database);
        var input = manifest.Validate(database); // Validate everything before opening a transaction.
        if (!(await HealthAsync(connection, database, ct)).Ready)
            throw new InvalidOperationException("Explicit schema migration or reconciliation required.");
        await using var db = Open(connection);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // No workspace exists on first install. Serialize provisioning before acquiring
        // the regular workspace lock, shared with API writers, in that fixed order.
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource='neo-orchestration:bootstrap',
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
            IF @result < 0 THROW 51001, 'Bootstrap transaction lock unavailable.', 1;
            """, ct);
        var created = 0;
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.Key == input.Organization.Key, ct);
        if (org is null) { org = input.Organization; db.Add(org); created++; }
        else if (!org.IsEnabled || org.Name != input.Organization.Name) throw new BootstrapConflictException();
        var workspace = await db.Workspaces.SingleOrDefaultAsync(x => x.OrganizationId == org.Id && x.Key == input.Workspace.Key, ct);
        if (workspace is null)
        {
            workspace = Workspace.Create(org, input.Workspace.Key, input.Workspace.Name);
            db.Add(workspace); created++;
        }
        else if (!workspace.IsEnabled || workspace.Name != input.Workspace.Name) throw new BootstrapConflictException();
        // New parents are visible to this transaction, but not committed on later failure.
        await db.SaveChangesAsync(ct);
        await WorkspaceTransaction.LockAsync(db, workspace.Scope, ct);
        var project = await db.Projects.SingleOrDefaultAsync(x => x.WorkspaceId == workspace.Id && x.Key == input.Project.Key, ct);
        if (project is null)
        {
            project = Project.Create(workspace, input.Project.Key, input.Project.Name);
            db.Add(project); created++;
        }
        else if (!project.IsEnabled || project.Name != input.Project.Name) throw new BootstrapConflictException();
        var roleIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var candidate in input.Roles)
        {
            var role = await db.Roles.SingleOrDefaultAsync(x => x.WorkspaceId == workspace.Id && x.Key == candidate.Key, ct);
            if (role is null)
            {
                role = RoleProfile.Create(workspace, candidate.Key, candidate.Name, candidate.ScopeDescription);
                db.Add(role); created++;
            }
            else if (!role.IsEnabled || role.Name != candidate.Name || role.ScopeDescription != candidate.ScopeDescription)
                throw new BootstrapConflictException();
            roleIds.Add(role.Key, role.Id);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(org.Id, workspace.Id, project.Id, roleIds, created);
    }
}
