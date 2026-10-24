using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

static class PipOperatorTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-operators", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-operators", "PIP operators").Id;
        access.AddUser(new(tenant, "alice", "Alice"));
        access.SetGrant(new(tenant, "alice", "organization.read", null), "admin", "direct grant must respect explicit PIP vetoes");
        var pip = new PipStore(db, access);
        var source = pip.Register(tenant, "directory", "https://operators.example.test/pip", "operators.example.test", null, enabled: true);
        var policies = new AccessPolicyStore(db, access, null, pip);
        PatternCommand Command(string attribute, string value, string effect, string op = "equals") => new(policies.Read(tenant).Revision,
            "operator fixture", "attribute", "organization.read", effect, null, null, null, null, null, null, "typed comparison fixture",
            AttributeName: attribute, AttributeValue: value, PipSourceId: source.Id, AttributeOperator: op);
        void Publish(PatternCommand command)
        {
            var state = policies.Draft(tenant, command, "author");
            policies.Transition(tenant, state.Patterns[^1].Id, state.Revision, "publish", "reviewer", "independent review");
        }
        Publish(Command("organization.unit", "engineering", "Permit"));
        Publish(Command("organization.unit", "engineering", "Deny", "not-equals"));
        Publish(Command("employment.active", "false", "Deny"));
        var now = new DateTimeOffset(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);
        var serial = 0;
        void Snapshot(bool active, string? unit)
        {
            now = now.AddSeconds(1);
            var values = new Dictionary<string, string> { ["employment.active"] = active ? "true" : "false" };
            if (unit is not null) values["organization.unit"] = unit;
            Save(pip, source, "alice", values, now, "operator-" + ++serial);
        }
        Snapshot(true, "engineering");
        check(policies.Evaluate(tenant, "alice", "organization.read", now).Decision == "Permit", "equal unit does not trigger the not-equals deny");
        Snapshot(false, "engineering");
        check(policies.Evaluate(tenant, "alice", "organization.read", now) is { Decision: "Deny", Reason: "explicit-deny" },
            "inactive employment veto overrides an engineering permit and a direct grant without changing membership");
        check(access.GetUsers(tenant).Single(x => x.KeycloakSubject == "alice").IsActive, "employment false remains distinct from local active membership");
        Snapshot(true, "finance");
        check(policies.Evaluate(tenant, "alice", "organization.read", now).Decision == "Deny", "unit transfer revokes earlier eligibility despite a direct grant");
        Snapshot(true, null);
        check(policies.Evaluate(tenant, "alice", "organization.read", now) is { Decision: "Indeterminate", Pip.Status: "missing-attribute" },
            "not-equals never treats a missing attribute as a successful comparison");
        Snapshot(true, "engineering");
        check(policies.Evaluate(tenant, "alice", "organization.read", now.AddSeconds(300)).Decision == "Indeterminate",
            "not-equals never treats expired evidence as a successful comparison or bypasses it with a grant");
        var revision = policies.Read(tenant).Revision;
        Reject<ArgumentException>(() => policies.Draft(tenant, Command("organization.unit", "engineering", "Deny", "contains"), "author"), check,
            "unsupported attribute operators are rejected at draft creation");
        Reject<ArgumentException>(() => policies.Draft(tenant, new(revision, "wrong template", "member-window", "organization.read", "Permit",
            null, null, now, null, null, null, "operator belongs to attribute rules", AttributeOperator: "not-equals"), "author"), check,
            "not-equals cannot be attached to a non-attribute template");
        check(policies.Read(tenant).Revision == revision, "rejected operators do not increment policy revision");
        var valid = policies.Read(tenant);
        var legacyJson = JsonSerializer.SerializeToNode(valid.Patterns[0])!.AsObject();
        legacyJson.Remove(nameof(AccessPattern.AttributeOperator));
        check(legacyJson.Deserialize<AccessPattern>()?.AttributeOperator == "equals", "persisted pre-operator policies deserialize with backward-compatible equals semantics");
        db.Transaction(() => { db.Put("access.policy", tenant.ToString(), valid with { Patterns = [valid.Patterns[0] with { AttributeOperator = "contains" }, .. valid.Patterns.Skip(1)] }); return true; });
        check(policies.Evaluate(tenant, "alice", "organization.read", now) is { Decision: "Indeterminate", Reason: "invalid-policy-data" },
            "invalid persisted operators fail closed before direct grant combination");
        db.Transaction(() => { db.Put("access.policy", tenant.ToString(), valid); return true; });
        TypedComparisons(db, access, check);
    }

    private static void TypedComparisons(FabricDatabase db, PersistentAccessManagement access, Action<bool, string> check)
    {
        var cases = new[]
        {
            (Type: "boolean", Left: "true", Equal: "true", Different: "false"),
            (Type: "integer", Left: "+01", Equal: "1", Different: "2"),
            (Type: "decimal", Left: "1.00", Equal: "1", Different: "1.1"),
            (Type: "datetime", Left: "2026-10-17T00:00:00Z", Equal: "2026-10-17T00:00:00+00:00", Different: "2026-10-17T00:00:01Z")
        };
        foreach (var item in cases)
        {
            var tenant = access.CreateTenant("operator-" + item.Type, "typed operator").Id;
            access.AddUser(new(tenant, "reader", "Reader"));
            access.SetGrant(new(tenant, "reader", "organization.read", null), "admin", "typed comparison fixture");
            var pip = new PipStore(db, access);
            var source = pip.Register(tenant, "typed", "https://operators.example.test/typed", "operators.example.test", null,
                enabled: true, definitions: [new("test.value", item.Type)]);
            var policies = new AccessPolicyStore(db, access, null, pip);
            var draft = policies.Draft(tenant, new(0, "typed not-equals", "attribute", "organization.read", "Deny", null, null, null, null, null, null,
                "typed", AttributeName: "test.value", AttributeValue: item.Equal, PipSourceId: source.Id, AttributeOperator: "not-equals"), "author");
            policies.Transition(tenant, draft.Patterns[^1].Id, draft.Revision, "publish", "reviewer", "typed review");
            var now = new DateTimeOffset(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);
            Save(pip, source, "reader", new Dictionary<string, string> { ["test.value"] = item.Left }, now, "same");
            check(policies.Evaluate(tenant, "reader", "organization.read", now).Decision == "Permit", item.Type + " not-equals uses typed equality, not textual spelling");
            Save(pip, source, "reader", new Dictionary<string, string> { ["test.value"] = item.Different }, now.AddSeconds(1), "different");
            check(policies.Evaluate(tenant, "reader", "organization.read", now.AddSeconds(1)).Decision == "Deny", item.Type + " not-equals denies a genuinely different typed value");
        }
    }

    private static void Save(PipStore store, PipSourceDefinition source, string subject, Dictionary<string, string> values, DateTimeOffset at, string version) =>
        store.SaveSnapshot(source, subject, new(source.TenantId, subject, 1, values, version, null, at,
            PipStore.HashAttributes(source.TenantId, subject, 1, values, version, at)), at);
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    {
        try { action(); } catch (T) { check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
}
