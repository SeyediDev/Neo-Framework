using System.Security.Cryptography;
using System.Text;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

static class PipPolicyTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "pip-policy"); Directory.CreateDirectory(root);
        using var db = new FabricDatabase(Path.Combine(root, "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-policy", "PIP policy").Id;
        access.AddUser(new AddTenantUserRequest(tenant, "alice", "Alice"));
        var store = new PipStore(db, access);
        var source = store.Register(tenant, "directory", "https://directory.example.test/pip", "directory.example.test", null, maxAgeSeconds: 30, enabled: true);
        var observed = DateTimeOffset.UtcNow.AddSeconds(-1);
        var values = new Dictionary<string, string> { ["employment.active"] = "true" };
        var version = "directory-1";
        var hash = Hash(tenant, "alice", values, version, observed);
        store.SaveSnapshot(source, new PipFetchedSnapshot(tenant, "alice", 1, values, version, null, observed, hash), observed);
        var policies = new AccessPolicyStore(db, access, null, store);
        var draft = policies.Draft(tenant, new PatternCommand(0, "active directory user", "attribute", "organization.read", "Permit", null, null, null, null, null, null,
            "external directory", null, "working-time", false, "employment.active", "true", source.Id), "author");
        policies.Transition(tenant, draft.Patterns[^1].Id, draft.Revision, "publish", "publisher", "reviewed");
        var permitted = policies.Evaluate(tenant, "alice", "organization.read", observed.AddSeconds(1));
        check(permitted.Decision == "Permit", "PIP attribute rule grants only when the fresh external attribute matches");
        check(permitted.Pip?.SourceId == source.Id && !System.Text.Json.JsonSerializer.Serialize(permitted.Pip).Contains("employment.active", StringComparison.Ordinal), "access decisions expose PIP provenance without disclosing external attribute values");
        check(policies.Evaluate(tenant, "alice", "organization.read", observed.AddSeconds(31)).Decision == "Indeterminate", "stale PIP attribute evidence fails closed");
        access.SetGrant(new(tenant, "alice", "organization.read", null), "admin", "fixture");
        check(policies.Evaluate(tenant, "alice", "organization.read", observed.AddSeconds(31)).Decision == "Indeterminate", "manual grants cannot bypass a stale required PIP attribute");
        var denyDraft = policies.Draft(tenant, new PatternCommand(policies.Read(tenant).Revision, "restricted department", "attribute", "organization.read", "Deny", null, null, null, null, null, null,
            "external directory", null, "working-time", false, "organization.unit", "restricted", source.Id), "author");
        policies.Transition(tenant, denyDraft.Patterns[^1].Id, denyDraft.Revision, "publish", "publisher", "reviewed");
        check(policies.Evaluate(tenant, "alice", "organization.read", observed.AddSeconds(1)).Decision == "Indeterminate",
            "missing optional attribute used by a deny policy cannot be bypassed by a permit or manual grant");
        store.SetEnabled(tenant, source.Id, false);
        check(policies.Evaluate(tenant, "alice", "organization.read", observed.AddSeconds(1)).Decision == "Indeterminate", "disabling a required PIP source denies access conservatively");
        check(PipSchema.ValuesEqual(new("level", "integer"), "+01", "1"), "integer PIP comparisons use numeric values rather than spelling");
        check(PipSchema.ValuesEqual(new("amount", "decimal"), "1.00", "1"), "decimal PIP comparisons ignore insignificant trailing zeros");
        check(PipSchema.ValuesEqual(new("observed", "datetime"), "2026-10-23T12:00:00Z", "2026-10-23T12:00:00+00:00"), "datetime PIP comparisons compare instants");
    }

    private static string Hash(Guid tenant, string subject, IReadOnlyDictionary<string, string> attributes, string version, DateTimeOffset observed)
    {
        return PipStore.HashAttributes(tenant, subject, 1, attributes, version, observed);
    }
}
