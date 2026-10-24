using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

// Dependency-free regression executable. Fixtures are authored independently of
// this implementation; golden checks compare every field, including nulls.
var schemas = new ContractSchemaValidator(Path.Combine(AppContext.BaseDirectory, "Schemas"));
var validator = new ContractProjectionValidator(schemas);
var evaluator = new ContractPolicyEvaluator(validator);
var pep = new ContractPolicyPep(schemas);
var projector = new ContractProjectionProjector(validator);
var projection = Fixture("projection.v1.json");
var context = Fixture("context.v1.json");
var projectionEvent = Fixture("projection-event.v1.json");
var projectionV2 = Fixture("integration-projection.v2.json");
var contextV2 = Fixture("integration-context.v2.json");
var eventV2 = Fixture("integration-projection-event.v2.json");
var goldenV2 = Fixture("integration-decision.v2.json");
const string Now = "2026-10-10T10:00:00.000Z";
const string NowV2 = "2026-10-10T06:29:59.000Z";
const string Admit = "urn:fanasa:obligation:admit-allocation:1";
const string Resource = "urn:fanasa:obligation:resource-tenant:1";
string[] fulfilled = [Admit, Resource];
var passed = 0;
var failed = 0;

foreach (var entry in Fixture("golden-decisions.v1.json")["cases"]!.AsArray())
{
    var name = Text(entry!["name"]);
    Test($"v1 complete golden DTO: {name}", () =>
    {
        var p = projection.DeepClone();
        var c = context.DeepClone();
        switch (name)
        {
            case "deny": Set(c, "membership.active", false); break;
            case "not-applicable": Set(c, "resource.productId", "other-product"); break;
            case "indeterminate": Remove(c, "environment.zone"); break;
        }
        var actual = Node(evaluator.EvaluateJson(p, c));
        EqualJson(entry["expected"], actual);
        Equal(0, schemas.Validate("decision.v1.schema.json", actual).Count);
        if (name != "permit") Equal(false, pep.CanExecuteJson(actual, fulfilled, Now));
    });
}

Test("v2 complete integration golden DTO", () =>
{
    var actual = Node(evaluator.EvaluateJson(projectionV2, contextV2));
    EqualJson(goldenV2, actual);
    Equal(0, schemas.Validate("decision.v2.schema.json", actual).Count);
    Equal(true, pep.CanExecuteJson(actual, fulfilled, "2026-10-10T06:30:00.000Z"));
});

Test("typed evaluator agrees with JSON evaluator", () =>
{
    EqualJson(Node(evaluator.EvaluateJson(projectionV2, contextV2)),
        Node(evaluator.Evaluate(Read<ContractAccessProjection>(projectionV2), Read<AccessEvaluationContext>(contextV2))));
});

Test("strict JSON helpers roundtrip both versioned projections", () =>
{
    foreach (var p in new[] { projection, projectionV2 })
    {
        var parsed = ContractJson.Parse(p.ToJsonString())!;
        var typed = Read<ContractAccessProjection>(parsed);
        EqualJson(p, ContractJson.ToNode(typed));
        EqualJson(p, ContractJson.ToNode(ContractJson.Copy(typed)));
    }
});
foreach (var lexical in new[] { "7.0", "7e0" })
    Test($"Safe integral JSON lexical form: {lexical}", () =>
    {
        var p = ContractJson.Parse(projectionV2.ToJsonString().Replace("\"contractVersion\":7", "\"contractVersion\":" + lexical, StringComparison.Ordinal))!;
        var c = ContractJson.Parse(contextV2.ToJsonString().Replace("\"contractVersion\":7", "\"contractVersion\":" + lexical, StringComparison.Ordinal))!;
        Equal(0, validator.ValidateJson(p).Count);
        EqualJson(goldenV2, Node(evaluator.EvaluateJson(p, c)));
        var d = ContractJson.Parse(goldenV2.ToJsonString().Replace("\"contractVersion\":7", "\"contractVersion\":" + lexical, StringComparison.Ordinal))!;
        Equal(true, pep.CanExecuteJson(d, fulfilled, "2026-10-10T06:30:00.000Z"));
        Equal(7L, Read<ContractAccessProjection>(p).ContractVersion);
    });
Test("nullable independent allocation revision accepts integral JSON lexical form", () =>
{
    var p = ContractJson.Parse(projectionV2.ToJsonString().Replace("\"configRevision\":1", "\"configRevision\":1e0", StringComparison.Ordinal))!;
    Equal(0, validator.ValidateJson(p).Count);
    EqualJson(goldenV2, Node(evaluator.EvaluateJson(p, contextV2)));
});
Test("long JSON converter preserves only finite safe integral values", () =>
{
    Equal(9007199254740991L, Read<PricingReference>(JsonNode.Parse("{\"priceId\":\"price\",\"priceVersion\":9007199254740991}")!).PriceVersion);
    EqualJson(JsonNode.Parse("{\"priceId\":\"price\",\"priceVersion\":9007199254740991}"), ContractJson.ToNode(new PricingReference("price", 9007199254740991L)));
    foreach (var lexical in new[] { "9007199254740992", "1.5", "\"1\"", "1e100" })
        Throws(() => Read<PricingReference>(JsonNode.Parse("{\"priceId\":\"price\",\"priceVersion\":" + lexical + "}")!));
    Throws(() => ContractJson.ToNode(new PricingReference("price", 9007199254740992L)));
});
Test("required constructor parameters cannot silently default", () =>
{
    Throws(() => Read<TenantAttributes>(JsonNode.Parse("{\"tenantId\":\"tenant-b\"}")!));
    Throws(() => Read<RateLimit>(JsonNode.Parse("{\"algorithm\":\"token-bucket\",\"requestsPerSecond\":5}")!));
});
foreach (var (name, invalid) in new[]
{
    ("duplicate", "{\"schemaVersion\":\"1.0.0\",\"schemaVersion\":\"2.0.0\"}"),
    ("escaped duplicate", "{\"schemaVersion\":1,\"schema\\u0056ersion\":2}"),
    ("nested duplicate", "{\"a\":{\"id\":1,\"id\":2}}"),
    ("comment", "{\"a\":/* no */1}"),
    ("trailing comma", "{\"a\":1,}"),
    ("deep nesting", new string('[', 65) + "0" + new string(']', 65)),
    ("node budget", "[" + string.Join(',', Enumerable.Repeat("0", 10_001)) + "]")
})
    Test($"Strict JSON rejects {name}", () => Throws(() => ContractJson.Parse(invalid)));
Test("strict JSON accepts separate object keys without conflating case", () =>
{
    EqualJson(JsonNode.Parse("[{\"id\":1},{\"id\":2},{\"ID\":3,\"id\":4}]"), ContractJson.Parse("[{\"id\":1},{\"id\":2},{\"ID\":3,\"id\":4}]"));
});
foreach (var (name, json) in new[]
{
    ("minimal schema version duplicate", "{\"schemaVersion\":\"2.0.0\",\"schemaVersion\":\"2.0.0\"}"),
    ("complete projection schema version duplicate", DuplicateRaw(projectionV2, "schemaVersion")),
    ("complete projection contract ID duplicate", projectionV2.ToJsonString().Replace("\"contractId\":", "\"contractId\":\"fixture-contract-b\",\"contractId\":", StringComparison.Ordinal))
})
{
    Test($"Externally parsed lazy duplicate schema boundary: {name}", () =>
    {
        var raw = JsonNode.Parse(json)!; var before = raw.ToJsonString();
        True(schemas.Validate("contract-access-projection.v2.schema.json", raw).Count > 0);
        Equal(before, raw.ToJsonString());
    });
    Test($"Externally parsed lazy duplicate projection boundary: {name}", () =>
    {
        var raw = JsonNode.Parse(json)!; var before = raw.ToJsonString();
        True(validator.ValidateJson(raw).Count > 0); Equal(before, raw.ToJsonString());
    });
    Test($"Externally parsed lazy duplicate evaluator boundary: {name}", () =>
    {
        var raw = JsonNode.Parse(json)!; var before = raw.ToJsonString(); var cBefore = contextV2.ToJsonString();
        var d = Node(evaluator.EvaluateJson(raw, contextV2));
        Equal("Indeterminate", Text(d["decision"])); DecisionIsValid(d);
        Equal(before, raw.ToJsonString()); Equal(cBefore, contextV2.ToJsonString());
    });
    Test($"Externally parsed lazy duplicate PEP boundary: {name}", () =>
    {
        var raw = JsonNode.Parse(json)!; var before = raw.ToJsonString();
        Equal(false, pep.CanExecuteJson(raw, fulfilled, NowV2)); Equal(before, raw.ToJsonString());
    });
}
foreach (var (name, json) in new[]
{
    ("environment", DuplicateRaw(contextV2, "environment")),
    ("resource", DuplicateRaw(contextV2, "resource")),
    ("requestId", DuplicateRaw(contextV2, "requestId")),
    ("nested product ID", contextV2.ToJsonString().Replace("\"productId\":\"organization-fabric\"", "\"productId\":\"organization-fabric\",\"productId\":\"organization-fabric\"", StringComparison.Ordinal)),
    ("nested zone", contextV2.ToJsonString().Replace("\"zone\":\"ir-private-1\"", "\"zone\":\"ir-private-1\",\"zone\":\"ir-private-1\"", StringComparison.Ordinal))
})
    Test($"Externally parsed lazy duplicate context boundary: {name}", () =>
    {
        var raw = JsonNode.Parse(json)!;
        var before = raw.ToJsonString(); var pBefore = projectionV2.ToJsonString();
        True(schemas.Validate("request-context.v2.schema.json", raw).Count > 0);
        var d = Node(evaluator.EvaluateJson(projectionV2, raw));
        Equal("Indeterminate", Text(d["decision"])); DecisionIsValid(d);
        Equal(before, raw.ToJsonString()); Equal(pBefore, projectionV2.ToJsonString());
    });
Test("PEP rejects a complete lazy duplicate decision without mutating it", () =>
{
    var raw = JsonNode.Parse(DuplicateRaw(goldenV2, "schemaVersion"))!; var before = raw.ToJsonString();
    Equal(false, pep.CanExecuteJson(raw, fulfilled, NowV2)); Equal(before, raw.ToJsonString());
});
foreach (var (time, expected) in new[]
{
    ("2026-10-10T10:00:00.000Z", true), ("2024-02-29T23:59:59.999Z", true),
    ("2026-02-29T10:00:00.000Z", false), ("2026-02-30T10:00:00.000Z", false),
    ("2026-10-10T24:00:00.000Z", false), ("2026-10-10T10:60:00.000Z", false),
    ("2026-10-10T10:00:60.000Z", false), ("2026-10-10T10:00:00Z", false),
    ("2026-10-10T10:00:00.000+00:00", false), ("2026-10-10t10:00:00.000z", false)
})
    Test($"Canonical UTC: {time}", () => Equal(expected, ContractJson.IsCanonicalUtc(time)));

foreach (var (name, schemaText) in new[]
{
    ("unknown assertion", "{\"type\":\"string\",\"minLenght\":1}"),
    ("unknown assertion in unused branch", "{\"if\":false,\"then\":{\"unsupported\":true},\"else\":true}"),
    ("cyclic reference", "{\"$ref\":\"probe.schema.json\"}"),
    ("remote reference", "{\"$ref\":\"https://example.invalid/schema.json\"}"),
    ("escaping local reference", "{\"$ref\":\"../outside.json\"}"),
    ("missing reference", "{\"$ref\":\"absent.json\"}"),
    ("duplicate schema property", "{\"type\":\"string\",\"type\":\"object\"}"),
    ("unsupported format", "{\"type\":\"string\",\"format\":\"email\"}")
})
    Test($"Schema bootstrap rejects {name}", () => InSchemas(new Dictionary<string, string> { ["probe.schema.json"] = schemaText }, dir => ThrowsData(() => new ContractSchemaValidator(dir))));

Test("schema local sibling and $defs references enforce all constraints", () => InSchemas(new Dictionary<string, string>
{
    ["shared.schema.json"] = "{\"$defs\":{\"positive\":{\"type\":\"integer\",\"minimum\":1}}}",
    ["probe.schema.json"] = "{\"$ref\":\"shared.schema.json#/$defs/positive\",\"maximum\":3}"
}, dir =>
{
    var local = new ContractSchemaValidator(dir);
    Equal(0, local.Validate("probe.schema.json", JsonValue.Create(2)).Count);
    True(local.Validate("probe.schema.json", JsonValue.Create(0)).Count > 0);
    True(local.Validate("probe.schema.json", JsonValue.Create(4)).Count > 0);
    True(local.Validate("probe.schema.json", JsonValue.Create("2")).Count > 0);
}));
Test("schema contains counts structurally matching elements", () => InSchemas(new Dictionary<string, string>
{
    ["probe.schema.json"] = "{\"type\":\"array\",\"contains\":{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"const\":\"admit\"}}},\"minContains\":1,\"maxContains\":1}"
}, dir =>
{
    var local = new ContractSchemaValidator(dir);
    Equal(0, local.Validate("probe.schema.json", JsonNode.Parse("[{\"id\":\"admit\"},{\"id\":\"resource\"}]")).Count);
    True(local.Validate("probe.schema.json", new JsonArray()).Count > 0);
    True(local.Validate("probe.schema.json", JsonNode.Parse("[{\"id\":\"admit\",\"value\":1},{\"id\":\"admit\",\"value\":2}]")).Count > 0);
}));
Test("schema safe numeric boundary and unknown filenames fail closed", () => InSchemas(new Dictionary<string, string>
{
    ["probe.schema.json"] = "{\"type\":\"number\"}"
}, dir =>
{
    var local = new ContractSchemaValidator(dir);
    Equal(0, local.Validate("probe.schema.json", JsonValue.Create(9007199254740991L)).Count);
    True(local.Validate("probe.schema.json", JsonValue.Create(9007199254740992L)).Count > 0);
    True(local.Validate("probe.schema.json", JsonValue.Create(double.PositiveInfinity)).Count > 0);
    True(local.Validate("absent.json", JsonValue.Create(1)).Count > 0);
    Throws(() => local.Validate("../probe.schema.json", JsonValue.Create(1)));
}));

(string Name, Action<JsonNode, JsonNode> Change)[] denialConditions =
[
    ("authentication", (_, c) => Set(c, "subject.authenticationValid", false)),
    ("OAuth client", (_, c) => Set(c, "subject.clientAuthorized", false)),
    ("audience", (_, c) => Set(c, "subject.audience", "another-api")),
    ("OAuth scope", (_, c) => Set(c, "subject.scopes", new JsonArray())),
    ("tenant inactive", (_, c) => Set(c, "tenant.active", false)),
    ("tenant identity", (_, c) => Set(c, "tenant.tenantId", "tenant-c")),
    ("resource tenant", (_, c) => Set(c, "resource.consumerTenantId", "tenant-c")),
    ("membership inactive", (_, c) => Set(c, "membership.active", false)),
    ("membership principal", (_, c) => Set(c, "membership.subjectId", "other-subject")),
    ("membership tenant", (_, c) => Set(c, "membership.tenantId", "tenant-c")),
    ("membership not yet valid", (_, c) => Set(c, "membership.validFrom", "2026-10-10T10:00:01.000Z")),
    ("membership expiry inclusive", (_, c) => Set(c, "membership.validTo", Now)),
    ("contract suspended", (p, _) => Set(p, "status", "Suspended")),
    ("contract ended", (p, _) => Set(p, "status", "Ended")),
    ("contract not yet valid", (p, _) => Set(p, "validFrom", "2026-10-10T10:00:01.000Z")),
    ("contract not yet effective", (p, _) => Set(p, "effectiveAt", "2026-10-10T10:00:01.000Z")),
    ("contract expiry inclusive", (p, _) => Set(p, "validTo", Now)),
    ("feature entitlement", (p, _) => Set(p, "featureEntitlements", new JsonArray())),
    ("eligible role", (p, _) => Set(p, "eligibleRoles", new JsonArray())),
    ("unsupported action", (_, c) => { Set(c, "action", "report.write"); Set(c, "subject.scopes", new JsonArray("report.write")); Set(c, "grant.actions", new JsonArray("report.write")); }),
    ("grant inactive", (_, c) => Set(c, "grant.active", false)),
    ("grant principal", (_, c) => Set(c, "grant.subjectId", "other-subject")),
    ("grant tenant", (_, c) => Set(c, "grant.consumerTenantId", "tenant-c")),
    ("grant product", (_, c) => Set(c, "grant.productId", "other-product")),
    ("grant action", (_, c) => Set(c, "grant.actions", new JsonArray())),
    ("grant role", (_, c) => Set(c, "grant.role", "OrganizationAdmin")),
    ("grant not yet valid", (_, c) => Set(c, "grant.validFrom", "2026-10-10T10:00:01.000Z")),
    ("grant expiry inclusive", (_, c) => Set(c, "grant.validTo", Now)),
    ("environment", (_, c) => Set(c, "resource.environment", "production")),
    ("operation group", (_, c) => Set(c, "resource.operationGroup", "administration")),
    ("zone", (_, c) => Set(c, "environment.zone", "other-zone"))
];
foreach (var (name, change) in denialConditions)
    Scenario($"AND condition: {name}", change, "Deny");

(string Name, Action<JsonNode, JsonNode> Change)[] invalidAttributes =
[
    ("authentication missing", (_, c) => Remove(c, "subject.authenticationValid")),
    ("tenant missing", (_, c) => Remove(c, "tenant")),
    ("membership missing", (_, c) => Remove(c, "membership")),
    ("grant missing", (_, c) => Remove(c, "grant")),
    ("evidence missing", (_, c) => Remove(c, "evidence")),
    ("resource product missing", (_, c) => Remove(c, "resource.productId")),
    ("zone missing", (_, c) => Remove(c, "environment.zone")),
    ("subject delimiter", (_, c) => Set(c, "subject.subjectId", "Subject;tenant=tenant-c")),
    ("zone delimiter", (_, c) => Set(c, "environment.zone", "ir-private-1;other-zone")),
    ("string boolean", (_, c) => Set(c, "tenant.active", "true")),
    ("unknown context property", (_, c) => Set(c, "platformAdmin", true)),
    ("invalid time", (_, c) => Set(c, "environment.now", "today")),
    ("impossible date", (_, c) => Set(c, "environment.now", "2026-02-30T10:00:00.000Z")),
    ("invalid membership date", (_, c) => Set(c, "membership.validFrom", "not-a-date")),
    ("invalid grant date", (_, c) => Set(c, "grant.validTo", "not-a-date")),
    ("expired evidence", (_, c) => Set(c, "evidence.validUntil", "2026-10-10T09:59:59.999Z")),
    ("evidence expiry inclusive", (_, c) => Set(c, "evidence.validUntil", Now)),
    ("stale contract evidence", (_, c) => Set(c, "evidence.contractVersion", 6)),
    ("stale policy evidence", (_, c) => Set(c, "evidence.policyVersion", 2))
];
foreach (var (name, change) in invalidAttributes)
    Scenario($"Fail closed: {name}", change, "Indeterminate");

Test("explicit Deny takes precedence over missing membership only for matching target", () =>
{
    var p = projection.DeepClone();
    var c = context.DeepClone();
    Set(p, "policy.explicitDenyActions", new JsonArray("report.read"));
    Equal("Deny", evaluator.EvaluateJson(p, c).Decision);
    Remove(c, "membership");
    Equal("Deny", evaluator.EvaluateJson(p, c).Decision);
    Set(c, "resource.productId", "other-product");
    Equal("NotApplicable", evaluator.EvaluateJson(p, c).Decision);
});

Test("validity windows include their start", () =>
{
    var p = projection.DeepClone();
    var c = context.DeepClone();
    Set(p, "validFrom", Now); Set(p, "effectiveAt", Now);
    Set(c, "membership.validFrom", Now); Set(c, "grant.validFrom", Now);
    Equal("Permit", evaluator.EvaluateJson(p, c).Decision);
});

(string Name, Action<JsonNode, JsonNode> Change, string Until)[] lifetimes =
[
    ("contract", (p, c) => { Set(p, "validTo", "2026-10-10T10:00:12.000Z"); Set(c, "evidence.validUntil", "2026-10-10T10:02:00.000Z"); }, "2026-10-10T10:00:12.000Z"),
    ("membership", (_, c) => Set(c, "membership.validTo", "2026-10-10T10:00:13.000Z"), "2026-10-10T10:00:13.000Z"),
    ("grant", (_, c) => Set(c, "grant.validTo", "2026-10-10T10:00:14.000Z"), "2026-10-10T10:00:14.000Z"),
    ("evidence", (_, _) => { }, "2026-10-10T10:00:45.000Z"),
    ("v1 ceiling", (_, c) => Set(c, "evidence.validUntil", "2026-10-10T10:02:00.000Z"), "2026-10-10T10:01:00.000Z")
];
foreach (var (name, change, until) in lifetimes)
    Test($"Permit lifetime: {name}", () =>
    {
        var p = projection.DeepClone(); var c = context.DeepClone(); change(p, c);
        var actual = Node(evaluator.EvaluateJson(p, c));
        Equal("Permit", Text(actual["decision"])); Equal(until, Text(actual["validUntil"]));
    });

var permit = Node(evaluator.EvaluateJson(projection, context));
(string Name, string[]? Fulfilled, string? Time, bool Expected)[] pepInputs =
[
    ("none fulfilled", [], Now, false),
    ("admission only", [Admit], Now, false),
    ("resource only", [Resource], Now, false),
    ("both fulfilled", fulfilled, Now, true),
    ("last millisecond", fulfilled, "2026-10-10T10:00:44.999Z", true),
    ("at expiry", fulfilled, "2026-10-10T10:00:45.000Z", false),
    ("after expiry", fulfilled, "2026-10-10T10:00:45.001Z", false),
    ("before evaluation", fulfilled, "2026-10-10T09:59:59.999Z", false),
    ("invalid clock", fulfilled, "invalid-time", false),
    ("missing clock", fulfilled, null, false),
    ("missing fulfillment collection", null, Now, false)
];
foreach (var (name, ids, time, expected) in pepInputs)
    Test($"PEP: {name}", () => Equal(expected, pep.CanExecuteJson(permit, ids, time)));

(string Name, Action<JsonNode> Change)[] malformedDecisions =
[
    ("unknown obligation", d => Set(d, "obligations.0.id", "urn:fanasa:obligation:unknown:1")),
    ("missing resource", d => Get(d, "obligations")!.AsArray().RemoveAt(1)),
    ("duplicate admission", d => Set(d, "obligations.1", Get(d, "obligations.0"))),
    ("duplicate resource", d => Set(d, "obligations.0", Get(d, "obligations.1"))),
    ("wrong fulfillment", d => Set(d, "obligations.0.fulfillOn", "Deny")),
    ("unknown schema", d => Set(d, "schemaVersion", "99.0.0")),
    ("unknown profile", d => Set(d, "profileId", "another-profile")),
    ("missing projection reference", d => Set(d, "projectionReference", null)),
    ("missing policy reference", d => Set(d, "policyReferences", new JsonArray())),
    ("missing deadline", d => Set(d, "validUntil", null)),
    ("TTL exceeded", d => Set(d, "validUntil", "2026-10-10T10:01:00.001Z")),
    ("unknown field", d => Set(d, "admin", true)),
    ("allocation substitution", d => Set(d, "allocationReference.allocationId", "other-allocation")),
    ("canonical scope forgery", d => Set(d, "obligations.0.parameters.canonicalScope", "tenant=tenant-c;products=club;environments=development;groups=reporting")),
    ("resource tenant substitution", d => Set(d, "obligations.1.parameters.consumerTenantId", "tenant-c")),
    ("resource product substitution", d => Set(d, "obligations.1.parameters.productId", "other-product")),
    ("resource environment substitution", d => Set(d, "obligations.1.parameters.environment", "production")),
    ("unsupported resource action", d => Set(d, "obligations.1.parameters.action", "admin")),
    ("invalid rate", d => Set(d, "obligations.0.parameters.rate.requestsPerSecond", 0))
];
foreach (var (name, change) in malformedDecisions)
    Test($"PEP malformed DTO: {name}", () =>
    {
        var d = permit.DeepClone(); change(d);
        Equal(false, pep.CanExecuteJson(d, [Admit, Resource, "urn:fanasa:obligation:unknown:1"], Now));
    });
foreach (var invalid in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["decision"] = "Permit" } })
    Test($"PEP invalid shape: {invalid?.ToJsonString() ?? "null"}", () => Equal(false, pep.CanExecuteJson(invalid, fulfilled, Now)));

foreach (var (name, change) in malformedDecisions.Take(4))
    Test($"Decision schema cardinality: {name}", () =>
    {
        var d = permit.DeepClone(); change(d);
        True(schemas.Validate("decision.v1.schema.json", d).Count > 0);
    });
Test("Decision schema rejects duplicate admissions with different values", () =>
{
    var d = permit.DeepClone(); Set(d, "obligations.1", Get(d, "obligations.0"));
    Set(d, "obligations.1.parameters.rate.requestsPerSecond", 121);
    True(schemas.Validate("decision.v1.schema.json", d).Count > 0);
});

(string Name, Action<JsonNode> Change)[] invalidProjections =
[
    ("unknown field", p => Set(p, "admin", true)),
    ("unsupported schema", p => Set(p, "schemaVersion", "99.0.0")),
    ("tenant substitution", p => { Set(p, "limits.scope.consumerTenantId", "tenant-c"); Recanonicalize(p); }),
    ("product substitution", p => { Set(p, "limits.scope.productIds", new JsonArray("hyper")); Recanonicalize(p); }),
    ("scope forgery", p => Set(p, "limits.canonicalScope", Text(Get(p, "limits.canonicalScope")) + ";groups=administration")),
    ("unsorted products", p => { Set(p, "limits.scope.productIds", new JsonArray("hyper", "club")); Recanonicalize(p); }),
    ("duplicate product", p => Set(p, "limits.scope.productIds", new JsonArray("club", "club"))),
    ("platform role", p => Set(p, "eligibleRoles", new JsonArray("PlatformAdmin"))),
    ("unsupported feature", p => Set(p, "featureEntitlements", new JsonArray("admin"))),
    ("unsupported policy action", p => Set(p, "policy.explicitDenyActions", new JsonArray("admin"))),
    ("negative seats", p => Set(p, "seats.maximum", -1)),
    ("zero rate", p => Set(p, "limits.rate.requestsPerSecond", 0)),
    ("zero burst", p => Set(p, "limits.rate.burstCapacity", 0)),
    ("fractional burst", p => Set(p, "limits.rate.burstCapacity", 1.5)),
    ("negative quota", p => Set(p, "limits.quota.requests", -1)),
    ("unsafe contract version", p => Set(p, "contractVersion", 9007199254740992L)),
    ("invalid date", p => Set(p, "validFrom", "not-a-date")),
    ("empty validity interval", p => Set(p, "validFrom", Get(p, "validTo"))),
    ("nonterminal effective at expiry", p => Set(p, "effectiveAt", Get(p, "validTo")))
];
foreach (var (name, change) in invalidProjections)
    Test($"Projection fail closed: {name}", () =>
    {
        var p = projection.DeepClone(); change(p);
        True(validator.ValidateJson(p).Count > 0);
        Equal("Indeterminate", evaluator.EvaluateJson(p, context).Decision);
    });
foreach (var invalid in new JsonNode?[] { null, new JsonObject(), new JsonArray(), JsonValue.Create("projection") })
    Test($"Projection invalid shape: {invalid?.ToJsonString() ?? "null"}", () => Equal("Indeterminate", evaluator.EvaluateJson(invalid, context).Decision));

Test("Club template rejects another product using Club audience", () =>
{
    var p = projection.DeepClone(); var c = context.DeepClone();
    Set(p, "productId", "other-product"); Set(p, "limits.scope.productIds", new JsonArray("other-product")); Recanonicalize(p);
    Set(c, "resource.productId", "other-product"); Set(c, "grant.productId", "other-product");
    True(evaluator.EvaluateJson(p, c).Decision != "Permit");
});

Test("canonical scope sorts dimensions without mutating input", () =>
{
    var scope = new JsonObject { ["consumerTenantId"] = "tenant-b", ["productIds"] = new JsonArray("hyper", "club"), ["environments"] = new JsonArray("production", "development"), ["operationGroups"] = new JsonArray("reporting", "lookup") };
    var typed = Read<AllocationScope>(scope); var before = Node(typed);
    Equal("tenant=tenant-b;products=club,hyper;environments=development,production;groups=lookup,reporting", ContractProjectionValidator.CanonicalScope(typed));
    EqualJson(before, Node(typed));
});
Test("terminal newline in identifiers and UTC month cannot bypass anchors", () =>
{
    var p = projectionV2.DeepClone(); Set(p, "contractId", Text(Get(p, "contractId")) + "\n");
    True(validator.ValidateJson(p).Count > 0); Equal("Indeterminate", evaluator.EvaluateJson(p, contextV2).Decision);
    var c = contextV2.DeepClone();
    foreach (var field in new[] { "subject.subjectId", "membership.subjectId", "grant.subjectId" }) Set(c, field, Text(Get(c, field)) + "\n");
    Equal("Indeterminate", evaluator.EvaluateJson(projectionV2, c).Decision);
    var scope = Read<AllocationScope>(Get(projectionV2, "limits.scope")!) with { ConsumerTenantId = Text(Get(projectionV2, "consumerTenantId")) + "\n" };
    Throws(() => ContractProjectionValidator.CanonicalScope(scope));
    Throws(() => validator.CounterKey(Read<AllocationLimits>(Get(projectionV2, "limits")!), "quota", "2026-10\n"));
});
Test("ordinary schema substring patterns still accept a matching substring", () => InSchemas(new Dictionary<string, string>
{
    ["probe.schema.json"] = "{\"type\":\"string\",\"pattern\":\"abc\"}"
}, dir => Equal(0, new ContractSchemaValidator(dir).Validate("probe.schema.json", JsonValue.Create("xabcx")).Count)));

(string Name, Action<JsonNode> Change)[] badScopes =
[
    ("missing tenant", s => Remove(s, "consumerTenantId")),
    ("null tenant", s => Set(s, "consumerTenantId", null)),
    ("tenant delimiter", s => Set(s, "consumerTenantId", "tenant-b;products=hyper")),
    ("list delimiter", s => Set(s, "productIds", new JsonArray("club,hyper"))),
    ("uppercase product", s => Set(s, "productIds", new JsonArray("Club"))),
    ("duplicate product", s => Set(s, "productIds", new JsonArray("club", "club"))),
    ("empty environment", s => Set(s, "environments", new JsonArray())),
    ("missing dimension", s => Remove(s, "operationGroups"))
];
foreach (var (name, change) in badScopes)
    Test($"Canonical scope rejects: {name}", () =>
    {
        var scope = Get(projection, "limits.scope")!.DeepClone(); change(scope);
        Throws(() => ContractProjectionValidator.CanonicalScope(Read<AllocationScope>(scope)));
    });

Test("counter identity ignores contract, policy, price and limit configuration amendments", () =>
{
    var p = projection.DeepClone();
    Set(p, "contractVersion", 8); Set(p, "policy.policyVersion", 4); Set(p, "pricingReference.priceVersion", 2);
    Set(p, "limits.rate.requestsPerSecond", 180); Set(p, "limits.quota.requests", 150000);
    var original = Read<AllocationLimits>(Get(projection, "limits")!);
    var amended = Read<AllocationLimits>(Get(p, "limits")!);
    Equal(validator.CounterKey(original, "rate"), validator.CounterKey(amended, "rate"));
    Equal(validator.CounterKey(original, "quota", "2026-10"), validator.CounterKey(amended, "quota", "2026-10"));
    True(validator.CounterKey(original, "quota", "2026-11") != validator.CounterKey(original, "quota", "2026-10"));
    Throws(() => validator.CounterKey(original, "quota"));
    Throws(() => validator.CounterKey(original, "quota", "2026-13"));
    Throws(() => validator.CounterKey(original, "rate", "2026-10"));
    Throws(() => validator.CounterKey(original, "unknown"));
});

Test("projector applies first snapshot and recognizes exact and reordered replay", () =>
{
    var input = Read<ContractProjectionEvent>(projectionEvent); var before = Node(input);
    var result = projector.Apply(null, input, "fanasa-contracts");
    Equal("applied", result.Status); EqualJson(projection, Node(result.State!.Projection));
    Equal(1, result.State.Receipts.Count); EqualJson(before, Node(input));
    Equal("215d8cec82c09b7b000fef5c772ff7a4dec4e988d702666cab2244f5dea59cf2", result.State.Receipts[0].Fingerprint);
    Equal("duplicate", projector.Apply(result.State, input, "fanasa-contracts").Status);
    var reordered = new JsonObject();
    foreach (var pair in projectionEvent.AsObject().Reverse()) reordered[pair.Key] = pair.Value?.DeepClone();
    Equal("duplicate", projector.Apply(result.State, Read<ContractProjectionEvent>(reordered), "fanasa-contracts").Status);
});
Test("v2 event fingerprint matches independently computed Node reference", () =>
{
    var state = Apply(null, eventV2).State!;
    Equal("656304ada012303240ad1dc70afb86b5a2bda2ce8a5aac2b96f6b3ec641dd497", state.Receipts[0].Fingerprint);
});

Test("projector remembers event and aggregate conflicts including equivalent snapshot IDs", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var reused = projectionEvent.DeepClone(); Set(reused, "projection.status", "Suspended");
    var conflict = Apply(first, reused);
    Equal("conflict", conflict.Status); EqualJson(Node(first), Node(conflict.State));
    Set(reused, "eventId", "another-event-7"); Equal("conflict", Apply(first, reused).Status);
    var equivalent = projectionEvent.DeepClone(); Set(equivalent, "eventId", "equivalent-event-7");
    var duplicate = Apply(first, equivalent); Equal("duplicate", duplicate.Status);
    Equal(first.Receipts.Count + 1, duplicate.State!.Receipts.Count);
    var advanced = ChangedEvent(8); Set(advanced, "eventId", "equivalent-event-7");
    Equal("conflict", Apply(duplicate.State, advanced).Status);
});

Test("stale snapshots cannot undo suspension and stale replay is remembered", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var suspension = ChangedEvent(8); Set(suspension, "projection.status", "Suspended"); Set(suspension, "projection.effectiveAt", "2026-10-10T09:00:00.000Z");
    var suspended = Apply(first, suspension); Equal("applied", suspended.Status);
    Equal("Deny", evaluator.EvaluateJson(Node(suspended.State!.Projection), context).Decision);
    var older = ChangedEvent(6); var stale = Apply(suspended.State, older);
    Equal("stale", stale.Status); EqualJson(suspension["projection"], Node(stale.State!.Projection));
    Equal(suspended.State.Receipts.Count + 1, stale.State.Receipts.Count);
    Equal("duplicate", Apply(stale.State, older).Status);
    Set(older, "correlationId", "different-correlation"); Equal("conflict", Apply(stale.State, older).Status);
});

Test("newer immediate suspension supersedes future activation", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var future = ChangedEvent(8); Set(future, "projection.effectiveAt", "2026-10-11T00:00:00.000Z");
    var scheduled = Apply(first, future); Equal("applied", scheduled.Status);
    var suspension = ChangedEvent(9); Set(suspension, "projection.status", "Suspended"); Set(suspension, "projection.effectiveAt", "2026-10-10T09:00:00.000Z");
    var suspended = Apply(scheduled.State, suspension); Equal("applied", suspended.Status);
    var c = context.DeepClone(); Set(c, "evidence.contractVersion", 9);
    Equal("Deny", evaluator.EvaluateJson(Node(suspended.State!.Projection), c).Decision);
    Set(c, "environment.now", "2026-10-11T10:00:00.000Z"); Set(c, "evidence.validUntil", "2026-10-11T10:00:45.000Z");
    Equal("Deny", evaluator.EvaluateJson(Node(suspended.State.Projection), c).Decision);
    var replay = Apply(suspended.State, future); Equal("duplicate", replay.Status);
    EqualJson(suspension["projection"], Node(replay.State!.Projection));
});

Test("snapshot gaps apply and Ended cannot resume", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var ended = ChangedEvent(10); Set(ended, "projection.status", "Ended"); Set(ended, "projection.effectiveAt", "2026-10-10T09:00:00.000Z");
    var terminal = Apply(first, ended); Equal("applied", terminal.Status);
    var rejected = Apply(terminal.State, ChangedEvent(11)); Equal("rejected", rejected.Status);
    EqualJson(Node(terminal.State), Node(rejected.State));
});

Test("terminal snapshot at contract expiry applies and remains terminal", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var ended = ChangedEvent(8); Set(ended, "projection.status", "Ended");
    Set(ended, "projection.effectiveAt", Get(ended, "projection.validTo")); Set(ended, "occurredAt", Get(ended, "projection.validTo"));
    Equal(0, validator.Validate(Read<ContractAccessProjection>(ended["projection"]!)).Count);
    var terminal = Apply(first, ended); Equal("applied", terminal.Status);
    Equal("duplicate", Apply(terminal.State, ended).Status);
    var resumed = ChangedEvent(9); Set(resumed, "projection.validTo", "2026-12-01T00:00:00.000Z");
    Set(resumed, "projection.effectiveAt", "2026-11-01T00:00:00.000Z"); Set(resumed, "occurredAt", "2026-11-01T00:00:00.000Z");
    Equal("rejected", Apply(terminal.State, resumed).Status);
});

(string Name, Action<JsonNode> Change)[] rejectedEvents =
[
    ("producer body substitution", e => Set(e, "producer", "untrusted-producer")),
    ("missing event ID", e => Remove(e, "eventId")),
    ("missing correlation ID", e => Remove(e, "correlationId")),
    ("invalid timestamp", e => Set(e, "occurredAt", "not-a-date")),
    ("event schema mismatch", e => Set(e, "schemaVersion", "2.0.0")),
    ("aggregate version mismatch", e => Set(e, "aggregateVersion", 9)),
    ("invalid quota", e => Set(e, "projection.limits.quota.requests", -1)),
    ("contract identity", e => Set(e, "projection.contractId", "another-contract")),
    ("owner identity", e => Set(e, "projection.ownerTenantId", "tenant-c")),
    ("subscription identity", e => Set(e, "projection.subscriptionId", "another-subscription")),
    ("allocation identity", e => Set(e, "projection.limits.allocationId", "another-allocation")),
    ("scope expansion", e => { Set(e, "projection.limits.scope.environments", new JsonArray("development", "production")); Recanonicalize(e["projection"]!); })
];
foreach (var (name, change) in rejectedEvents)
    Test($"Projector rejects: {name}", () =>
    {
        var state = Apply(null, projectionEvent).State!; var before = Node(state);
        var e = ChangedEvent(8); change(e); var result = Apply(state, e);
        Equal("rejected", result.Status); True(result.Errors.Count > 0);
        EqualJson(before, Node(result.State)); EqualJson(before, Node(state));
    });
Test("projector authenticates producer independently from body", () =>
{
    var input = Read<ContractProjectionEvent>(projectionEvent);
    Equal("rejected", projector.Apply(null, input, "untrusted-producer").Status);
    Equal("rejected", projector.Apply(null, input, null).Status);
    Equal("rejected", projector.Apply(null, null, "fanasa-contracts").Status);
});

Test("v1 valid amendment preserves counter identity", () =>
{
    var original = Apply(null, projectionEvent).State!;
    var e = ChangedEvent(8); Set(e, "projection.effectiveAt", "2026-10-10T09:00:00.000Z");
    Set(e, "projection.pricingReference.priceVersion", 2); Set(e, "projection.limits.rate.requestsPerSecond", 180); Set(e, "projection.limits.quota.requests", 150000);
    var next = Apply(original, e); Equal("applied", next.Status);
    Equal(validator.CounterKey(original.Projection!.Limits, "rate"), validator.CounterKey(next.State!.Projection!.Limits, "rate"));
    Equal(validator.CounterKey(original.Projection.Limits, "quota", "2026-10"), validator.CounterKey(next.State.Projection.Limits, "quota", "2026-10"));
    var c = context.DeepClone(); Set(c, "evidence.contractVersion", 8);
    Equal("Permit", evaluator.EvaluateJson(Node(next.State.Projection), c).Decision);
});

Test("v2 preserves literal uppercase UUID principal without broadening product keys", () =>
{
    var c = contextV2.DeepClone(); const string uuid = "ABCDEFAB-1234-4321-ABCD-ABCDEFABCDEF";
    Set(c, "subject.subjectId", uuid); Set(c, "membership.subjectId", uuid); Set(c, "grant.subjectId", uuid);
    Equal("Permit", evaluator.EvaluateJson(projectionV2, c).Decision); Equal(uuid, Text(Get(c, "subject.subjectId")));
    Set(c, "requestId", Get(projectionV2, "consumerTenantId")); Equal("Indeterminate", evaluator.EvaluateJson(projectionV2, c).Decision);
    var p = projectionV2.DeepClone(); Set(p, "productId", Get(p, "consumerTenantId"));
    Equal("Indeterminate", evaluator.EvaluateJson(p, contextV2).Decision);
});

(string Name, Action<JsonNode> Change, string Expected)[] v2Gates =
[
    ("wrong audience", c => Set(c, "subject.audience", "fanasa-club-api"), "Deny"),
    ("wrong consumer", c => Set(c, "resource.consumerTenantId", Get(projectionV2, "ownerTenantId")), "Deny"),
    ("wrong membership principal", c => Set(c, "membership.subjectId", Get(projectionV2, "ownerTenantId")), "Deny"),
    ("inactive grant", c => Set(c, "grant.active", false), "Deny"),
    ("unsupported action", c => Set(c, "action", "organization.unit.delete"), "Deny"),
    ("grant missing", c => Remove(c, "grant"), "Indeterminate"),
    ("stale policy evidence", c => Set(c, "evidence.policyVersion", 4), "Indeterminate")
];
foreach (var (name, change, expected) in v2Gates)
    Test($"v2 authority gate: {name}", () =>
    {
        var c = contextV2.DeepClone(); change(c); var d = Node(evaluator.EvaluateJson(projectionV2, c));
        Equal(expected, Text(d["decision"])); Equal(0, d["obligations"]!.AsArray().Count);
        Equal(false, pep.CanExecuteJson(d, fulfilled, NowV2));
    });
Test("v2 explicit Deny overrides missing membership", () =>
{
    var p = projectionV2.DeepClone(); var c = contextV2.DeepClone();
    Set(p, "policy.explicitDenyActions", new JsonArray("organization.unit.save")); Remove(c, "membership");
    Equal("Deny", evaluator.EvaluateJson(p, c).Decision);
});

foreach (var revision in new object?[] { null, 0, -1, 1.5, "1", 9007199254740992L })
    Test($"v2 configRevision invalid: {revision ?? "missing"}", () =>
    {
        var p = projectionV2.DeepClone();
        if (revision is null) Remove(p, "limits.configRevision"); else Set(p, "limits.configRevision", revision);
        Equal("Indeterminate", evaluator.EvaluateJson(p, contextV2).Decision);
    });
Test("v2 contract and policy versions are independent of configRevision", () =>
{
    var first = Apply(null, eventV2).State!;
    var e = ChangedEvent(8, true); Set(e, "projection.policy.policyVersion", 4);
    var next = Apply(first, e); Equal("applied", next.Status);
    Equal(1L, Get(Node(next.State!.Projection), "limits.configRevision")!.GetValue<long>());
});

Test("v2 config revisions conflict, advance and reject regression without identity reset", () =>
{
    var first = Apply(null, eventV2).State!; var before = Node(first);
    var e = ChangedEvent(8, true); Set(e, "projection.limits.rate.requestsPerSecond", 6);
    var conflict = Apply(first, e); Equal("conflict", conflict.Status); EqualJson(before, Node(conflict.State));
    Set(e, "projection.limits.configRevision", 2); var advanced = Apply(first, e); Equal("applied", advanced.Status);
    var staleConfig = Apply(advanced.State, ChangedEvent(9, true)); Equal("rejected", staleConfig.Status);
    EqualJson(Node(advanced.State), Node(staleConfig.State)); True(staleConfig.Errors.Count > 0);
    Equal(validator.CounterKey(first.Projection!.Limits, "rate"), validator.CounterKey(advanced.State!.Projection!.Limits, "rate"));
    Equal(validator.CounterKey(first.Projection.Limits, "quota", "2026-10"), validator.CounterKey(advanced.State.Projection.Limits, "quota", "2026-10"));
});
Test("v2 lower aggregate and lower config revision is stale and replay is remembered", () =>
{
    var first = Apply(null, eventV2).State!;
    var e = ChangedEvent(8, true); Set(e, "projection.limits.configRevision", 2); Set(e, "projection.limits.rate.requestsPerSecond", 6);
    var advanced = Apply(first, e).State!; var older = ChangedEvent(6, true);
    var stale = Apply(advanced, older); Equal("stale", stale.Status); EqualJson(Node(advanced.Projection), Node(stale.State!.Projection));
    Equal(advanced.Receipts.Count + 1, stale.State.Receipts.Count); Equal("duplicate", Apply(stale.State, older).Status);
});
Test("v2 suspend and resume preserve allocation and revision", () =>
{
    var first = Apply(null, eventV2).State!;
    var e = ChangedEvent(8, true); Set(e, "projection.status", "Suspended"); Set(e, "projection.policy.policyVersion", 4);
    var suspended = Apply(first, e); Equal("applied", suspended.Status);
    Equal("Deny", evaluator.EvaluateJson(Node(suspended.State!.Projection), contextV2).Decision);
    var resumed = Apply(suspended.State, ChangedEvent(9, true)); Equal("applied", resumed.Status);
    foreach (var state in new[] { suspended.State, resumed.State! })
    {
        Equal(1L, Get(Node(state.Projection), "limits.configRevision")!.GetValue<long>());
        Equal(validator.CounterKey(first.Projection!.Limits, "quota", "2026-10"), validator.CounterKey(state.Projection!.Limits, "quota", "2026-10"));
    }
});
Test("schema migration requires explicit handling", () =>
{
    var first = Apply(null, projectionEvent).State!;
    var migrated = eventV2.DeepClone();
    // Schema mismatch is rejected before accepting any changed aggregate tuple.
    var result = Apply(first, migrated); Equal("rejected", result.Status); EqualJson(Node(first), Node(result.State));
});
Test("v2 quota zero remains mandatory deny-all admission limit", () =>
{
    var p = projectionV2.DeepClone(); Set(p, "limits.quota.requests", 0);
    Equal(0, validator.Validate(Read<ContractAccessProjection>(p)).Count);
    var d = Node(evaluator.EvaluateJson(p, contextV2)); Equal("Permit", Text(d["decision"]));
    Equal(0L, Get(d, "obligations.0.parameters.quota.requests")!.GetValue<long>());
    Equal(false, pep.CanExecuteJson(d, [Resource], NowV2));
});

(string Name, Action<JsonNode> Change)[] v2PepChanges =
[
    ("missing resource", d => Get(d, "obligations")!.AsArray().RemoveAt(1)),
    ("wrong consumer", d => Set(d, "obligations.1.parameters.consumerTenantId", Get(projectionV2, "ownerTenantId"))),
    ("missing config revision", d => Remove(d, "obligations.0.parameters.configRevision")),
    ("unsupported calendar period", d => Set(d, "obligations.0.parameters.quota.period", "day")),
    ("multiple policies", d => Get(d, "policyReferences")!.AsArray().Add(new JsonObject { ["policyId"] = "other-policy", ["policyVersion"] = 1 })),
    ("TTL exceeded", d => Set(d, "validUntil", "2026-10-10T06:30:29.001Z"))
];
foreach (var (name, change) in v2PepChanges)
    Test($"v2 PEP rejects: {name}", () =>
    {
        var d = goldenV2.DeepClone(); change(d); Equal(false, pep.CanExecuteJson(d, fulfilled, "2026-10-10T06:30:00.000Z"));
    });
Test("v2 TTL ceiling and evidence expiry are bounded", () =>
{
    Equal(false, pep.CanExecuteJson(goldenV2, fulfilled, "2026-10-10T06:30:29.000Z"));
    Equal(false, pep.CanExecuteJson(goldenV2, [Admit], "2026-10-10T06:30:00.000Z"));
    var c = contextV2.DeepClone(); Set(c, "evidence.validUntil", "2026-10-10T06:30:00.500Z");
    Equal("2026-10-10T06:30:00.500Z", Text(Node(evaluator.EvaluateJson(projectionV2, c))["validUntil"]));
});

Test("evaluation and projection do not mutate caller inputs", () =>
{
    var p = projectionV2.DeepClone(); var c = contextV2.DeepClone();
    var pBefore = p.DeepClone(); var cBefore = c.DeepClone();
    evaluator.EvaluateJson(p, c); EqualJson(pBefore, p); EqualJson(cBefore, c);
    var typedProjection = Read<ContractAccessProjection>(p); var typedContext = Read<AccessEvaluationContext>(c);
    var typedProjectionBefore = Node(typedProjection); var typedContextBefore = Node(typedContext);
    var decision = evaluator.Evaluate(typedProjection, typedContext);
    decision.Obligations[0].Parameters["quota"]!["requests"] = 0;
    EqualJson(typedProjectionBefore, Node(typedProjection)); EqualJson(typedContextBefore, Node(typedContext));
    var e = Read<ContractProjectionEvent>(eventV2); var eBefore = Node(e);
    var first = projector.Apply(null, e, "fanasa-contracts").State!;
    var firstBefore = Node(first); var amended = Read<ContractProjectionEvent>(ChangedEvent(8, true));
    projector.Apply(first, amended, "fanasa-contracts");
    EqualJson(eBefore, Node(e)); EqualJson(firstBefore, Node(first));
    if (first.Projection.Limits.Scope.Environments is IList<string> returned)
    {
        returned[0] = "production";
        EqualJson(eBefore, Node(e));
    }
});

Test("shared bootstrap services remain deterministic under concurrent calls", () =>
{
    var p = Read<ContractAccessProjection>(projectionV2);
    var c = Read<AccessEvaluationContext>(contextV2);
    var e = Read<ContractProjectionEvent>(eventV2);
    var expectedState = Node(projector.Apply(null, e, "fanasa-contracts").State);
    Parallel.For(0, 64, _ =>
    {
        var actual = evaluator.Evaluate(p, c); EqualJson(goldenV2, Node(actual));
        True(pep.CanExecute(actual, fulfilled, "2026-10-10T06:30:00.000Z"));
        EqualJson(expectedState, Node(projector.Apply(null, e, "fanasa-contracts").State));
        Equal(0, validator.Validate(p).Count);
    });
});

AdapterTests.Run(Test, Path.Combine(AppContext.BaseDirectory, "Schemas"),
    Path.Combine(AppContext.BaseDirectory, "Fixtures"));

Console.WriteLine($"Contract policy .NET: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}

void Scenario(string name, Action<JsonNode, JsonNode> change, string expected)
{
    Test(name, () =>
    {
        var p = projection.DeepClone(); var c = context.DeepClone(); change(p, c);
        var actual = Node(evaluator.EvaluateJson(p, c)); Equal(expected, Text(actual["decision"]));
        Equal(0, actual["obligations"]!.AsArray().Count); Equal(false, pep.CanExecuteJson(actual, fulfilled, Now));
    });
}

ProjectionResult Apply(ProjectionState? state, JsonNode input)
{
    // Typed callers can supply null required values. Preserve that invalid shape
    // for validator tests without being stopped by the JSON required-name check.
    var copy = input.DeepClone();
    foreach (var required in new[] { "eventId", "correlationId" })
        if (!copy.AsObject().ContainsKey(required)) copy[required] = null;
    return projector.Apply(state, Read<ContractProjectionEvent>(copy), "fanasa-contracts");
}

JsonNode ChangedEvent(long version, bool v2 = false)
{
    var e = (v2 ? eventV2 : projectionEvent).DeepClone();
    Set(e, "eventId", v2 ? $"fixture-organization-event-{version}" : $"sample-event-{version}");
    Set(e, "aggregateVersion", version); Set(e, "projection.contractVersion", version);
    return e;
}

void Recanonicalize(JsonNode p) => Set(p, "limits.canonicalScope", ContractProjectionValidator.CanonicalScope(Read<AllocationScope>(Get(p, "limits.scope")!)));

void DecisionIsValid(JsonNode d)
{
    var filename = Text(d["schemaVersion"]) == "2.0.0" ? "decision.v2.schema.json" : "decision.v1.schema.json";
    Equal(0, schemas.Validate(filename, d).Count);
}

static string DuplicateRaw(JsonNode baseline, string property) => baseline.ToJsonString().Insert(1,
    JsonSerializer.Serialize(property) + ":" + baseline[property]!.ToJsonString() + ",");

static JsonNode Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;
static T Read<T>(JsonNode node) => node.Deserialize<T>(ContractJson.Options) ?? throw new InvalidOperationException($"Cannot read {typeof(T).Name}");
static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, ContractJson.Options)!;
static string Text(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidOperationException("Expected string");
static JsonNode? Get(JsonNode node, string path)
{
    JsonNode? current = node;
    foreach (var key in path.Split('.')) current = current is JsonArray array ? array[int.Parse(key)] : current?[key];
    return current;
}
static void Set(JsonNode node, string path, object? value)
{
    var keys = path.Split('.'); var current = node;
    foreach (var key in keys[..^1]) current = current is JsonArray array ? array[int.Parse(key)]! : current[key]!;
    var item = value is JsonNode json ? json.DeepClone() : JsonSerializer.SerializeToNode(value);
    if (current is JsonArray target) target[int.Parse(keys[^1])] = item; else current[keys[^1]] = item;
}
static void Remove(JsonNode node, string path)
{
    var keys = path.Split('.'); var current = node;
    foreach (var key in keys[..^1]) current = current is JsonArray array ? array[int.Parse(key)]! : current[key]!;
    if (current is JsonArray target) target.RemoveAt(int.Parse(keys[^1])); else current.AsObject().Remove(keys[^1]);
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
}
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
static void EqualJson(JsonNode? expected, JsonNode? actual)
{
    if (!JsonNode.DeepEquals(expected, actual)) throw new InvalidOperationException($"JSON differs. Expected: {expected?.ToJsonString()}; actual: {actual?.ToJsonString()}");
}
static void Throws(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    catch (JsonException) { return; }
    throw new InvalidOperationException("Expected invalid input to throw ArgumentException or JsonException");
}
static void ThrowsData(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected unsupported schema to throw InvalidDataException");
}
static void InSchemas(IReadOnlyDictionary<string, string> files, Action<string> body)
{
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    var directory = Path.GetFullPath(Path.Combine(temporaryRoot, "fanasa-policy-tests-" + Guid.NewGuid().ToString("N")));
    if (!directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test temporary directory");
    Directory.CreateDirectory(directory);
    try
    {
        foreach (var pair in files) File.WriteAllText(Path.Combine(directory, pair.Key), pair.Value);
        body(directory);
    }
    finally { Directory.Delete(directory, recursive: true); }
}
