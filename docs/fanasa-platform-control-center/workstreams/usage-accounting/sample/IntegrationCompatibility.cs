using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UsageAccounting.Sample;

// Local fixture evidence only. These files are not authenticated runtime resolvers.
public sealed class IntegrationEvidence(JsonElement binding, JsonElement decision, JsonElement? admission, FixtureBackend backend)
{
    private const long SafeInteger = 9007199254740991;
    private const string Admit = "urn:fanasa:obligation:admit-allocation:1";
    private const string Tenant = "urn:fanasa:obligation:resource-tenant:1";

    public void ValidateCommitted(FixtureEvent message)
    {
        ValidateAuthorization(message);
        var committed = backend.Query(message.BusinessKey);
        Require(committed is not null, "No committed backend business evidence exists.");
        var original = new FixtureEvent(committed!.Event);
        Require(committed.BusinessKey == message.BusinessKey && original.OperationFingerprint == message.OperationFingerprint,
            "Committed evidence does not bind this exact business operation, result and snapshot.");
    }

    public void ValidateAuthorization(FixtureEvent message)
    {
        // Reject duplicate object names before trusting fields or comparing reference strings.
        _ = FixtureEvent.SnapshotFingerprint(binding); _ = FixtureEvent.SnapshotFingerprint(decision);
        Require(admission is not null, "No admission witness exists.");
        var witness = admission!.Value; _ = FixtureEvent.SnapshotFingerprint(witness);
        Require(S(binding, "schemaVersion") == "fanasa.integration.fixture-binding/1" && B(binding, "synthetic"), "Unsupported common binding.");
        Require(S(decision, "schemaVersion") == "2.0.0" && S(decision, "profileId") == "fanasa.contract-policy/2", "Unsupported Decision DTO version/profile.");
        Require(S(witness, "schemaVersion") == "fanasa.gateway-admission.fixture.v1" && B(witness, "synthetic"), "Unsupported admission DTO version.");
        Require(S(decision, "decision") == "Permit", "Only Permit can support a new debit.");
        Require(B(witness, "allowed") && B(witness, "confirmed") && S(witness, "code") == "OK", "Admission was not unambiguously confirmed.");

        var requestId = S(decision, "requestId");
        Require(Regex.IsMatch(requestId, "^g-[a-f0-9]{32}$", RegexOptions.CultureInvariant), "Uncorrelated Gateway nonce.");
        Require(S(witness, "decisionId") == requestId && S(witness, "requestId") == requestId
            && S(witness, "admissionId") == "admission:" + requestId, "Admission does not reference the evaluated nonce.");
        Require(message.Text("authorization", "decisionId") == requestId
            && message.Text("authorization", "admissionId") == S(witness, "admissionId"), "Usage reference does not resolve to this Decision/admission.");
        var references = A(decision, "policyReferences");
        Require(references.Length == 1, "Exactly one selected policy reference is supported; composition needs a defined mapping.");
        var policy = references[0]; var policyVersion = Positive(policy, "policyVersion");
        Require(S(policy, "policyId") == S(binding, "policyId") && policyVersion == Positive(binding, "policyVersion"), "Policy reference binding mismatch.");
        var policyReference = S(policy, "policyId") + "@" + policyVersion.ToString(CultureInfo.InvariantCulture);
        Require(message.Text("authorization", "policyVersion") == policyReference && S(witness, "policyVersion") == policyReference, "Usage policy reference syntax/binding mismatch.");

        var projection = At(decision, "projectionReference"); var allocation = At(decision, "allocationReference");
        var contractVersion = ContractVersion(projection);
        Require(contractVersion == ContractVersion(binding) && contractVersion == ContractVersion(message.Payload)
            && contractVersion == ContractVersion(witness), "Contract version binding mismatch.");
        Require(S(projection, "contractId") == S(binding, "contractId") && message.Text("contractId") == S(binding, "contractId")
            && S(witness, "contractId") == S(binding, "contractId"), "Contract id binding mismatch.");

        foreach (var field in new[] { "ownerTenantId", "consumerTenantId" })
        {
            Require(Guid.TryParse(S(binding, field), out _) && S(witness, field) == S(binding, field)
                && message.Text(field) == S(binding, field), "Tenant binding mismatch: " + field);
        }
        Require(message.Product == S(binding, "productId") && S(witness, "productId") == message.Product
            && message.Environment == S(binding, "environment") && S(witness, "environment") == message.Environment
            && S(witness, "action") == S(binding, "action") && message.Text("operation", "version") == S(binding, "action") + "/v1",
            "Product/environment/action binding mismatch.");
        Require(Guid.TryParse(message.OperationId, out _) && Guid.TryParse(message.Text("operation", "requestId"), out _)
            && message.OperationId != requestId && message.Text("operation", "requestId") != requestId, "Business/attempt UUID must remain distinct from the Gateway nonce.");
        Require(Guid.TryParse(S(witness, "subjectId"), out _) && message.Actor == S(witness, "subjectId"), "Authenticated principal UUID binding mismatch.");

        var obligations = A(decision, "obligations");
        Require(obligations.Length == 2 && obligations.Select(x => S(x, "id")).Distinct(StringComparer.Ordinal).Count() == 2
            && obligations.All(x => S(x, "id") is Admit or Tenant && S(x, "fulfillOn") == "Permit"), "Missing, duplicate or unsupported obligation.");
        var limits = At(obligations.Single(x => S(x, "id") == Admit), "parameters");
        var resource = At(obligations.Single(x => S(x, "id") == Tenant), "parameters");
        foreach (var field in new[] { "consumerTenantId", "productId", "environment", "action" })
            Require(S(resource, field) == S(binding, field) && S(At(witness, "resourceTenant"), field) == S(binding, field), "Resource tenant obligation/witness mismatch: " + field);
        foreach (var value in new[] { S(allocation, "allocationId"), S(limits, "allocationId"), S(witness, "allocationId"), message.Text("allocationId") })
            Require(value == S(binding, "allocationId"), "Allocation reference binding mismatch.");
        foreach (var value in new[] { S(allocation, "canonicalScope"), S(limits, "canonicalScope"), S(witness, "allocationScope") })
            Require(value == S(binding, "canonicalScope"), "Canonical allocation scope binding mismatch.");
        var scope = At(limits, "scope");
        Require(S(scope, "consumerTenantId") == S(binding, "consumerTenantId"), "Allocation consumer scope mismatch.");
        foreach (var pair in new[] { ("productIds", "productId"), ("environments", "environment"), ("operationGroups", "operationGroup") })
            Require(A(scope, pair.Item1).Select(x => x.GetString()).SequenceEqual(new[] { S(binding, pair.Item2) }), "Allocation scope set mismatch: " + pair.Item1);
        var revision = Positive(binding, "configRevision");
        Require(Positive(limits, "configRevision") == revision && Positive(witness, "configRevision") == revision, "Independent allocation configRevision binding mismatch.");
        Require(S(limits, "meter") == S(binding, "admissionMeter") && S(limits, "unit") == "request"
            && message.Meter == S(binding, "billableMeter") && message.Unit == "change" && message.Quantity == 1
            && message.Text("operation", "billableBoundary") == S(binding, "billableBoundary"), "Admission and committed-change meter boundaries differ.");
        var rate = At(limits, "rate"); var quota = At(limits, "quota");
        Require(S(rate, "algorithm") == "token-bucket" && Number(rate, "requestsPerSecond") > 0
            && Number(rate, "requestsPerSecond") == Number(binding, "requestsPerSecond")
            && Positive(rate, "burstCapacity") == Positive(binding, "burstCapacity"), "Rate/burst binding mismatch.");
        Require(S(quota, "algorithm") == "fixed-calendar-utc" && S(quota, "period") == "month"
            && N(quota, "requests") == N(binding, "quotaRequests") && N(quota, "requests") > 0, "No successful witness for zero/malformed quota or unsupported period.");

        var evaluatedAt = Time(decision, "evaluatedAt"); var validUntil = Time(decision, "validUntil");
        var admittedAt = Time(witness, "admittedAt"); var deadline = Time(witness, "decisionValidUntil");
        var tokenExpiresAt = Positive(witness, "tokenExpiresAt");
        Require(tokenExpiresAt <= 253402300799, "Token expiry cannot exceed representable ISO time.");
        var start = new DateTimeOffset(evaluatedAt.Year, evaluatedAt.Month, 1, 0, 0, 0, TimeSpan.Zero); var end = start.AddMonths(1);
        var period = start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Require(Time(binding, "periodStart") == start && Time(binding, "periodEnd") == end
            && N(witness, "periodStartMs") == start.ToUnixTimeMilliseconds() && N(witness, "periodEndMs") == end.ToUnixTimeMilliseconds()
            && S(binding, "allocationPeriodId") == period && S(witness, "allocationPeriodId") == period && message.Text("allocationPeriodId") == period,
            "Allocation period/calendar binding mismatch.");
        Require(evaluatedAt == Time(binding, "evaluatedAt") && message.AcceptedAt == Time(binding, "backendAcceptedAt")
            && admittedAt >= evaluatedAt && admittedAt <= message.AcceptedAt && deadline > admittedAt
            && deadline <= validUntil && deadline <= evaluatedAt.AddSeconds(30) && message.AcceptedAt < deadline
            && deadline <= DateTimeOffset.FromUnixTimeSeconds(tokenExpiresAt)
            && admittedAt >= start && message.AcceptedAt < end, "Authorization was expired or not valid when backend accepted the operation.");
        // Current intake time is intentionally absent: historic committed evidence is not repriced/revoked by transport delay.
    }

    internal static JsonElement Load(string path)
    { using var document = JsonDocument.Parse(File.ReadAllText(path)); var value = document.RootElement.Clone(); _ = FixtureEvent.SnapshotFingerprint(value); return value; }
    internal static JsonElement Change(JsonElement value, Action<JsonObject> update)
    { var node = JsonNode.Parse(value.GetRawText())!.AsObject(); update(node); using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    internal static JsonElement At(JsonElement value, params string[] path)
    {
        foreach (var segment in path)
        { if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) throw new InvalidDataException("Missing evidence field: " + string.Join('.', path)); }
        return value;
    }
    internal static string S(JsonElement value, params string[] path)
    { var item = At(value, path); return item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException("Expected evidence string: " + string.Join('.', path)); }
    private static bool B(JsonElement value, string field)
    { var item = At(value, field); return item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw new InvalidDataException("Expected evidence boolean: " + field); }
    private static JsonElement[] A(JsonElement value, string field)
    { var item = At(value, field); return item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : throw new InvalidDataException("Expected evidence array: " + field); }
    private static long N(JsonElement value, string field)
    { var item = At(value, field); Require(item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number >= 0 && number <= SafeInteger, "Expected bounded exact integer: " + field); return item.GetInt64(); }
    private static long Positive(JsonElement value, string field)
    { var number = N(value, field); Require(number > 0, "Positive version/revision required: " + field); return number; }
    private static int ContractVersion(JsonElement value)
    { var number = Positive(value, "contractVersion"); Require(number <= int.MaxValue, "contractVersion must fit 1..2147483647 without truncation."); return checked((int)number); }
    private static decimal Number(JsonElement value, string field)
    { var item = At(value, field); Require(item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out _), "Expected finite decimal evidence number: " + field); return item.GetDecimal(); }
    private static DateTimeOffset Time(JsonElement value, string field)
    {
        var text = S(value, field);
        Require(Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "Expected valid ISO instant: " + field);
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUniversalTime();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

public static class IntegrationCompatibility
{
    public static void Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("--integration requires binding, generated Decision, generated admission and fresh output directory.");
        var paths = args.Select(Path.GetFullPath).ToArray(); var output = paths[3];
        var sample = Path.GetDirectoryName(typeof(IntegrationCompatibility).Assembly.Location)!;
        while (!File.Exists(Path.Combine(sample, "UsageAccounting.Sample.csproj"))) sample = Directory.GetParent(sample)?.FullName ?? throw new DirectoryNotFoundException("Sample root missing.");
        if (!output.StartsWith(sample + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Integration output must stay within sample/.");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a fresh integration output directory.");
        var binding = IntegrationEvidence.Load(paths[0]); var decision = IntegrationEvidence.Load(paths[1]); var admission = IntegrationEvidence.Load(paths[2]);
        var baseline = FixtureEvent.Load(Path.Combine(sample, "../fixtures/usage-event-v1.json"));
        var policy = IntegrationEvidence.At(decision, "policyReferences").EnumerateArray().Single();
        var message = baseline.Change(x =>
        {
            foreach (var field in new[] { "ownerTenantId", "consumerTenantId", "environment", "contractId", "allocationId", "allocationPeriodId" }) x[field] = IntegrationEvidence.S(binding, field);
            x["productKey"] = IntegrationEvidence.S(binding, "productId"); x["contractVersion"] = IntegrationEvidence.At(decision, "projectionReference", "contractVersion").GetInt64();
            x["eventId"] = "00000000-0000-4000-8000-000000000201";
            x["operation"]!["id"] = "55555555-5555-4555-8555-555555555555";
            x["operation"]!["requestId"] = "66666666-6666-4666-8666-666666666666";
            x["operation"]!["actorId"] = IntegrationEvidence.S(admission, "subjectId");
            x["operation"]!["acceptedAt"] = IntegrationEvidence.S(binding, "backendAcceptedAt");
            x["authorization"]!["decisionId"] = IntegrationEvidence.S(decision, "requestId");
            x["authorization"]!["policyVersion"] = IntegrationEvidence.S(policy, "policyId") + "@" + IntegrationEvidence.At(policy, "policyVersion").GetInt64().ToString(CultureInfo.InvariantCulture);
            x["authorization"]!["admissionId"] = IntegrationEvidence.S(admission, "admissionId");
            x["evidence"]!["businessReceiptId"] = "fixture-business-receipt-common-001";
            x["evidence"]!["reference"] = "fixture://organization/55555555-5555-4555-8555-555555555555";
        });
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("FAIL " + label); checks.Add("PASS " + label); Console.WriteLine("PASS " + label); }
        var backend = new FixtureBackend(Path.Combine(output, "backend-committed-evidence.json"));
        using var context = new FixtureContext(Path.Combine(output, "accounting"), message, 10000, 18, initialize: true);
        var pinned = new PinnedHistoricalResolver(message);
        ProposedUsageIntake Intake(JsonElement selectedDecision, JsonElement? selectedAdmission, FixtureBackend? selectedBackend = null) =>
            new(context.Database, context.Accounting, pinned, new FixtureClock(),
                new IntegrationEvidence(binding, selectedDecision, selectedAdmission, selectedBackend ?? backend).ValidateCommitted);
        string Snapshot() => JsonSerializer.Serialize(new { Account = context.Accounting.Read(message.Consumer), Owner = context.Accounting.Read(message.Owner),
            Receipts = context.Database.All<IntakeReceipt>(ProposedUsageIntake.Receipts), Usage = context.Database.All<RecordedUsage>(ProposedUsageIntake.Usage),
            Operations = context.Database.All<ChargedOperation>(ProposedUsageIntake.Operations), Outbox = context.Database.Outbox() });
        void NoDebit(string label, JsonElement selectedDecision, JsonElement? selectedAdmission, FixtureEvent? selectedMessage = null)
        {
            var before = Snapshot();
            try { Intake(selectedDecision, selectedAdmission).Accept(selectedMessage ?? message); }
            catch (InvalidDataException) { Check(Snapshot() == before && context.Accounting.Read(message.Consumer).Outstanding == 0, label + ": no debit/receipt/usage/outbox mutation"); return; }
            throw new InvalidOperationException("Expected invalid evidence: " + label);
        }
        new IntegrationEvidence(binding, decision, admission, backend).ValidateAuthorization(message);
        Check(true, "actual policy Decision and generated Gateway witness match the common binding and separate meters");
        NoDebit("Permit/admission alone before committed business evidence", decision, admission);
        try { backend.ExecuteThenLoseResponse(message); } catch (TimeoutException) { Check(true, "synthetic backend commits and flushes evidence before losing its response"); }
        Check(backend.Executions == 1 && context.Accounting.Read(message.Consumer).Outstanding == 0, "backend commit alone produces no accounting debit");

        NoDebit("Deny", IntegrationEvidence.Change(decision, x => x["decision"] = "Deny"), admission);
        NoDebit("missing admission", decision, null);
        foreach (var change in new Action<JsonObject>[] { x => x["confirmed"] = false, x => x["allowed"] = false, x => x["code"] = "UNKNOWN", x => x["admissionId"] = "admission:unknown" })
            NoDebit("unknown/failed/uncorrelated admission", decision, IntegrationEvidence.Change(admission, change));
        NoDebit("wrong consumer", decision, admission, message.Change(x => x["consumerTenantId"] = "99999999-9999-4999-8999-999999999999"));
        foreach (var field in new[] { "ownerTenantId", "productKey", "environment", "contractId", "allocationId", "allocationPeriodId" })
            NoDebit("wrong Usage binding " + field, decision, admission, message.Change(x => x[field] = field == "ownerTenantId" ? "99999999-9999-4999-8999-999999999999" : "wrong-binding"));
        foreach (var field in new[] { "decisionId", "policyVersion", "admissionId" })
            NoDebit("nonempty unresolved reference " + field, decision, admission, message.Change(x => x["authorization"]![field] = "unresolved-reference"));
        NoDebit("missing resource obligation", IntegrationEvidence.Change(decision, x => x["obligations"]!.AsArray().RemoveAt(1)), admission);
        NoDebit("wrong resource tenant", IntegrationEvidence.Change(decision, x => x["obligations"]![1]!["parameters"]!["consumerTenantId"] = "99999999-9999-4999-8999-999999999999"), admission);
        NoDebit("unknown obligation", IntegrationEvidence.Change(decision, x => x["obligations"]![1]!["id"] = "urn:unknown"), admission);
        NoDebit("duplicate obligation", IntegrationEvidence.Change(decision, x => x["obligations"]![1] = x["obligations"]![0]!.DeepClone()), admission);
        NoDebit("unhandled multiple policies", IntegrationEvidence.Change(decision, x => x["policyReferences"]!.AsArray().Add(x["policyReferences"]![0]!.DeepClone())), admission);
        foreach (var value in new long[] { 0, 2147483648, 9007199254740991 })
            NoDebit("out-of-range contractVersion " + value, IntegrationEvidence.Change(decision, x => x["projectionReference"]!["contractVersion"] = value), admission);
        NoDebit("fractional contractVersion", IntegrationEvidence.Change(decision, x => x["projectionReference"]!["contractVersion"] = 7.5), admission);
        NoDebit("wrong contractVersion", decision, admission, message.Change(x => x["contractVersion"] = 8));
        NoDebit("zero policyVersion", IntegrationEvidence.Change(decision, x => x["policyReferences"]![0]!["policyVersion"] = 0), admission);
        NoDebit("unsafe policyVersion", IntegrationEvidence.Change(decision, x => x["policyReferences"]![0]!["policyVersion"] = 9007199254740992L), admission);
        NoDebit("zero allocation revision", IntegrationEvidence.Change(decision, x => x["obligations"]![0]!["parameters"]!["configRevision"] = 0), admission);
        NoDebit("allocation revision inferred from contract", IntegrationEvidence.Change(decision, x => x["obligations"]![0]!["parameters"]!["configRevision"] = 7), admission);
        NoDebit("witness revision mismatch", decision, IntegrationEvidence.Change(admission, x => x["configRevision"] = 2));
        NoDebit("witness resource context mismatch", decision, IntegrationEvidence.Change(admission, x => x["resourceTenant"]!["consumerTenantId"] = "99999999-9999-4999-8999-999999999999"));
        NoDebit("witness allocation scope mismatch", decision, IntegrationEvidence.Change(admission, x => x["allocationScope"] = "route-only"));
        NoDebit("witness calendar bound mismatch", decision, IntegrationEvidence.Change(admission, x => x["periodEndMs"] = 1793577599999L));
        NoDebit("witness token expiry before deadline", decision, IntegrationEvidence.Change(admission, x => x["tokenExpiresAt"] = 1791613799L));
        NoDebit("wrong principal UUID", decision, admission, message.Change(x => x["operation"]!["actorId"] = "99999999-9999-4999-8999-999999999999"));
        NoDebit("unsupported Decision DTO version", IntegrationEvidence.Change(decision, x => x["schemaVersion"] = "1.0.0"), admission);
        NoDebit("unsupported quota period", IntegrationEvidence.Change(decision, x => x["obligations"]![0]!["parameters"]!["quota"]!["period"] = "day"), admission);
        NoDebit("zero quota cannot produce a successful witness", IntegrationEvidence.Change(decision, x => x["obligations"]![0]!["parameters"]!["quota"]!["requests"] = 0), admission);
        NoDebit("missing limits", IntegrationEvidence.Change(decision, x => x["obligations"]![0]!["parameters"]!.AsObject().Remove("rate")), admission);
        NoDebit("expired Decision at backend acceptance", IntegrationEvidence.Change(decision, x => x["validUntil"] = "2026-10-10T06:29:59Z"), admission);
        NoDebit("uncommitted outcome", decision, admission, message.Change(x => x["outcome"] = "unknown"));
        NoDebit("fabricated business receipt", decision, admission, message.Change(x => x["evidence"]!["businessReceiptId"] = "fabricated"));
        NoDebit("unbound rate snapshot", decision, admission, message.Change(x => x["rateSnapshot"]!["unitPrice"] = "99"));

        var intake = Intake(decision, admission); var charged = intake.Accept(message);
        Check(charged.Disposition == "charged" && charged.Amount == 12, "one committed common operation charges the existing artificial 12 IRR snapshot");
        var after = Snapshot(); var replay = intake.Accept(message);
        Check(replay.Disposition == "event-replay" && replay.ChargeId == charged.ChargeId && Snapshot() == after, "identical event replay creates no additional accounting mutation");
        var alias = message.Change(x => { x["eventId"] = "00000000-0000-4000-8000-000000000202"; x["producedAt"] = "2026-10-10T09:00:00Z"; x["operation"]!["requestId"] = "33333333-3333-4333-8333-333333333399"; });
        var aliasResult = intake.Accept(alias);
        Check(aliasResult.Disposition == "operation-replay" && aliasResult.ChargeId == charged.ChargeId
            && context.Database.All<IntakeReceipt>(ProposedUsageIntake.Receipts).Length == 2, "new delivery/attempt for the same committed operation adds only an alias receipt");
        Check(Intake(decision, null).Accept(message).Disposition == "event-replay",
            "recorded financial replay uses its immutable receipt when the admission resolver is unavailable, without creating a debit");
        var entries = context.Accounting.Read(message.Consumer).Account.Entries.Where(x => x.Kind == "usage").ToArray();
        Check(entries.Length == 1 && entries[0].Amount == 12 && context.Accounting.Read(message.Consumer).Outstanding == 12
            && context.Accounting.Read(message.Owner).Outstanding == 0 && context.Database.All<RecordedUsage>(ProposedUsageIntake.Usage).Length == 1
            && context.Database.Outbox().Count(x => x.Kind == ProposedUsageIntake.ChargedKind) == 1 && backend.Executions == 1,
            "final reconciliation: one business execution, one consumer debit, one usage/outbox, owner uncharged");
        File.WriteAllText(Path.Combine(output, "integration-usage-event.v1.json"), JsonSerializer.Serialize(message.Payload, Options));
        File.WriteAllText(Path.Combine(output, "integration-ledger.v1.json"), JsonSerializer.Serialize(new { Synthetic = true, DebitCount = entries.Length,
            Total = context.Accounting.Read(message.Consumer).Outstanding, Currency = message.Currency, Charged = charged, Charges = entries,
            OwnerOutstanding = context.Accounting.Read(message.Owner).Outstanding, BackendExecutions = backend.Executions, Receipts = 2,
            UsageRecords = 1, ChargedOutboxRecords = 1, BillableBoundary = message.Text("operation", "billableBoundary") }, Options));
        File.WriteAllLines(Path.Combine(output, "integration-checks.txt"), checks);
        var sources = new[] { paths[0], paths[1], paths[2], Path.Combine(sample, "IntegrationCompatibility.cs"), Path.Combine(sample, "ProposedUsageIntake.cs"),
            Path.Combine(sample, "Program.cs"), Path.Combine(sample, "FixtureEvent.cs"), Path.Combine(sample, "UnknownOutcome.cs"), Path.Combine(sample, "run-integration.ps1"),
            Path.Combine(sample, "../schema-check/validate.py"), Path.Combine(sample, "../fixtures/usage-event-v1.json"),
            Path.Combine(sample, "../schemas/usage-event-v1.schema.json"), Path.Combine(output, "integration-usage-event.v1.json"), Path.Combine(output, "integration-ledger.v1.json"),
            Path.Combine(output, "backend-committed-evidence.json"), Path.Combine(output, "integration-checks.txt") };
        File.WriteAllText(Path.Combine(output, "integration-evidence.v1.json"), JsonSerializer.Serialize(new { SchemaVersion = "fanasa.usage-integration-evidence/1", Synthetic = true,
            Checks = checks.Count, InputBinding = paths[0], Decision = paths[1], Admission = paths[2],
            Files = sources.Select(path => new { Path = Path.GetFullPath(path), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() }),
            Limits = new[] { "Local generated fixtures only; producer/PDP/Gateway authentication and real rate provenance remain pending.",
                "Proposed snapshot append uses existing Fabric/Accounting models; no product runtime wiring or production debit API installed.",
                "Native APISIX/Redis/TLS, backend data-tenant enforcement, real product/contract acceptance and settlement remain pending." } }, Options));
        Console.WriteLine($"{checks.Count} integration checks passed. Artifacts: {output}");
    }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
