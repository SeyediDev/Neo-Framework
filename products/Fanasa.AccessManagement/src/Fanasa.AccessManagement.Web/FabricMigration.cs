using System.Text.Json;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;

namespace Fanasa.AccessManagement.Web.Persistence;

public static class FabricMigration
{
    // Explicit import, never overwrites committed database documents and never removes source files.
    public static void ImportTenant(FabricDatabase database, Guid tenant, string organizationDirectory, string accountingDirectory)
    {
        var orgPath = Path.Combine(organizationDirectory, tenant + ".json");
        var billingPath = Path.Combine(accountingDirectory, tenant + ".billing.json");
        var state = File.Exists(orgPath) ? JsonSerializer.Deserialize<OrgState>(File.ReadAllText(orgPath)) ?? throw new InvalidDataException() : null;
        var billing = File.Exists(billingPath) ? JsonSerializer.Deserialize<BillingAccount>(File.ReadAllText(billingPath)) ?? throw new InvalidDataException() : null;
        if (state is null && billing is null) throw new FileNotFoundException("No legacy state found.");
        if ((state is not null && state.TenantId != tenant) || (billing is not null && billing.TenantId != tenant)) throw new InvalidDataException("Tenant identity mismatch.");
        var snapshots = new List<OrgState>();
        if (state is not null)
            for (long revision = 1; revision <= state.Revision; revision++)
            {
                var snapshot = JsonSerializer.Deserialize<OrgState>(File.ReadAllText(Path.Combine(organizationDirectory, tenant + ".history", revision + ".json"))) ?? throw new InvalidDataException();
                if (snapshot.TenantId != tenant || snapshot.Revision != revision) throw new InvalidDataException("Historical identity mismatch.");
                snapshots.Add(snapshot);
            }
        database.Transaction(() =>
        {
            if (database.Read<OrgState>("organization", tenant.ToString()) is not null || database.Read<BillingAccount>("accounting", tenant.ToString()) is not null) throw new InvalidOperationException("Target tenant already contains state.");
            if (state is not null) database.Put("organization", tenant.ToString(), state);
            foreach (var snapshot in snapshots) database.Put("organization.history", $"{tenant}:{snapshot.Revision}", snapshot);
            if (billing is not null) database.Put("accounting", tenant.ToString(), billing);
            database.Emit("legacy-import:" + tenant, "LegacyFabricImported", new { TenantId = tenant, OrganizationRevision = state?.Revision, AccountingRevision = billing?.Revision }, DateTimeOffset.UtcNow);
            return true;
        });
    }
}
