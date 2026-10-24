using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

namespace Fanasa.HrReadPdp.Tests;

public static class SchemaTests
{
    public static int Run()
    {
        var schemas = new ContractSchemaValidator(Path.Combine(AppContext.BaseDirectory, "Schemas"));
        const string schema = "hr-read-pdp-request.v1.schema.json";
        var count = 0;
        void Check(bool pass, string label) { count++; if (!pass) throw new InvalidOperationException("PDP schema test failed: " + label); }
        var request = WireTests.Fixture(WireTests.Settings());
        Check(schemas.Validate(schema, request).Count == 0, "actual chart fixture matches request schema");
        var history = request.DeepClone().AsObject();
        history["rawTarget"] = history["rawTarget"]!.GetValue<string>().Replace("/chart", "/history/1", StringComparison.Ordinal);
        history["action"] = "organization.hr.history.read";
        Check(schemas.Validate(schema, history).Count == 0, "history route/action fixture matches request schema");
        foreach (var field in request.Select(x => x.Key))
        {
            var missing = request.DeepClone().AsObject(); missing.Remove(field);
            Check(schemas.Validate(schema, missing).Count > 0, "required " + field);
        }
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("transportVersion", JsonValue.Create("future")), ("scope", JsonValue.Create("admin")),
            ("method", JsonValue.Create("POST")), ("tokenSha256", JsonValue.Create(new string('A', 64))),
            ("configRevision", JsonValue.Create(0)), ("tokenExpiresAt", JsonValue.Create(1.25)),
            ("ownerTenantId", JsonValue.Create("11111111-1111-4111-8111-111111111111")),
            ("action", JsonValue.Create("organization.hr.history.read")), ("authenticationValid", JsonValue.Create(true))
        })
        {
            var changed = request.DeepClone().AsObject(); changed[field] = value;
            Check(schemas.Validate(schema, changed).Count > 0, "structural contradiction rejected: " + field);
        }
        var unknownOwnership = request.DeepClone().AsObject(); unknownOwnership["ownership"]!["active"] = true;
        Check(schemas.Validate(schema, unknownOwnership).Count > 0, "ownership fields closed");
        return count;
    }
}
