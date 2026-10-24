using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;
using Microsoft.IdentityModel.Tokens;

// Source-pinned, local synthetic integration. The actual packaged Lua PEP and signer
// consume a real authenticated PCC Decision. Quota admission is a counted test double;
// this suite does not execute APISIX, Redis, a real IdP or a commercial tariff.
var options = PilotOptions.Parse(args);
var cases = new List<PilotCase>();
if (Pilot.HashFile(options.Bundle) != Pilot.ZipHash) throw new InvalidOperationException("Native handoff ZIP pin differs.");

await Run("packaged PEP and signer commit one correlated organization Usage and debit", async () =>
{
    await using var host = await GatewayFixture.Start();
    var command = host.Command(); var outboxBefore = host.Database.Outbox(take: 100).Length;
    var native = await Pilot.Authorize(host, command, options);
    Pilot.AssertAuthorized(native);
    using var response = await host.SendWitness(native.Witness());
    Pilot.Equal(HttpStatusCode.OK, response.StatusCode);
    Pilot.AssertCommitted(host, command, native, outboxBefore);
});

await Run("one native envelope commits once concurrently and replay tombstone survives reopen", async () =>
{
    await using var host = await GatewayFixture.Start();
    var command = host.Command(); var native = await Pilot.Authorize(host, command, options);
    var responses = await Task.WhenAll(host.SendWitness(native.Witness()), host.SendWitness(native.Witness()));
    try
    {
        Pilot.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.OK));
        Pilot.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Forbidden));
        Pilot.Equal(1L, host.Organization.Read(host.Tenant).Revision);
        Pilot.Equal(12m, host.Billing.Read(host.Tenant).Outstanding);
        Pilot.Equal(1, host.Database.All<MeasuredUsageOperation>("measured.operation").Length);
        var before = host.BusinessSnapshot();
        using var replay = await host.SendWitness(native.Witness());
        Pilot.Equal(HttpStatusCode.Forbidden, replay.StatusCode); Pilot.Equal(before, host.BusinessSnapshot());
        using var reopened = new FabricDatabase(host.DatabasePath);
        Pilot.Equal(1, reopened.All<JsonElement>(Pilot.ReplayBucket).Length);
    }
    finally { foreach (var response in responses) response.Dispose(); }
});

await Run("fresh native admission for exact business retry preserves original response and debit", async () =>
{
    await using var host = await GatewayFixture.Start(); var command = host.Command();
    var firstNative = await Pilot.Authorize(host, command, options);
    using var first = await host.SendWitness(firstNative.Witness()); Pilot.Equal(HttpStatusCode.OK, first.StatusCode);
    var original = await first.Content.ReadAsStringAsync(); var before = host.BusinessSnapshot();
    var retry = await Pilot.Authorize(host, command, options);
    Pilot.Check(firstNative.Nonce != retry.Nonce, "Business retry did not receive a fresh quota witness.");
    using var response = await host.SendWitness(retry.Witness());
    Pilot.Equal(HttpStatusCode.OK, response.StatusCode); Pilot.Equal(original, await response.Content.ReadAsStringAsync());
    Pilot.Equal(before, host.BusinessSnapshot()); Pilot.Equal(2, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("fresh native envelope cannot reuse a business UUID with a changed payload", async () =>
{
    await using var host = await GatewayFixture.Start(); var command = host.Command();
    var native = await Pilot.Authorize(host, command, options);
    using var first = await host.SendWitness(native.Witness()); Pilot.Equal(HttpStatusCode.OK, first.StatusCode);
    var changed = command with { Name = "Changed synthetic payload" }; var before = host.BusinessSnapshot();
    var changedNative = await Pilot.Authorize(host, changed, options);
    using var response = await host.SendWitness(changedNative.Witness());
    Pilot.Check(!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.InternalServerError, "Conflicting receipt was accepted.");
    Pilot.Equal(before, host.BusinessSnapshot());
});

await Run("tampering raw body after native signing is denied without business writes", async () =>
{
    await using var host = await GatewayFixture.Start(); var native = await Pilot.Authorize(host, host.Command(), options);
    var before = host.BusinessSnapshot(); var witness = native.Witness();
    using var response = await host.SendWitness(witness with { Body = witness.Body + " " });
    Pilot.Equal(HttpStatusCode.Forbidden, response.StatusCode); Pilot.Equal(before, host.BusinessSnapshot());
    Pilot.Equal(0, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("role revoked after native Permit and admission is denied by current PCC authority", async () =>
{
    await using var host = await GatewayFixture.Start(); var native = await Pilot.Authorize(host, host.Command(), options);
    host.AssignRole(false); var before = host.BusinessSnapshot();
    using var response = await host.SendWitness(native.Witness());
    Pilot.Equal(HttpStatusCode.Forbidden, response.StatusCode); Pilot.Equal(before, host.BusinessSnapshot());
});

await Run("native witness expiry after real admission rolls back and retains consumed nonce", async () =>
{
    await using var host = await GatewayFixture.Start("late-admission-expiry");
    var native = await Pilot.Authorize(host, host.Command(), options); var before = host.BusinessSnapshot();
    using var response = await host.SendWitness(native.Witness());
    Pilot.Equal(HttpStatusCode.Forbidden, response.StatusCode); Pilot.Check(host.ClockWasChanged, "Real admission hook was not reached.");
    Pilot.Equal(before, host.BusinessSnapshot()); Pilot.Equal(1, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("native witness expiry during real Usage validation rolls back all shared writes", async () =>
{
    await using var host = await GatewayFixture.Start("late-write-expiry");
    var native = await Pilot.Authorize(host, host.Command(), options); var before = host.BusinessSnapshot();
    using var response = await host.SendWitness(native.Witness());
    Pilot.Equal(HttpStatusCode.Forbidden, response.StatusCode); Pilot.Check(host.ClockWasChanged, "Real Usage validation hook was not reached.");
    Pilot.Equal(before, host.BusinessSnapshot()); Pilot.Equal(1, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("historical rate expiry keeps native receipt retry stable and denies new business", async () =>
{
    await using var host = await GatewayFixture.Start("tariff-expiry"); var command = host.Command();
    var native = await Pilot.Authorize(host, command, options);
    using var first = await host.SendWitness(native.Witness()); Pilot.Equal(HttpStatusCode.OK, first.StatusCode);
    var original = await first.Content.ReadAsStringAsync(); var before = host.BusinessSnapshot();
    host.AdvanceClock(TimeSpan.FromMinutes(3));
    var retry = await Pilot.Authorize(host, command, options);
    using var response = await host.SendWitness(retry.Witness());
    Pilot.Equal(HttpStatusCode.OK, response.StatusCode); Pilot.Equal(original, await response.Content.ReadAsStringAsync());
    Pilot.Equal(before, host.BusinessSnapshot());
    var fresh = await Pilot.Authorize(host, host.Command(), options);
    using var denied = await host.SendWitness(fresh.Witness());
    Pilot.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Pilot.Equal(before, host.BusinessSnapshot());
});

await Run("actual native Policy Deny never calls quota admission or emits a signed envelope", async () =>
{
    await using var host = await GatewayFixture.Start(); host.AssignRole(false);
    var before = host.BusinessSnapshot(); var native = await Pilot.Authorize(host, host.Command(), options);
    Pilot.AssertNoEnvelope(native, 0); Pilot.Equal(before, host.BusinessSnapshot());
    Pilot.Equal(0, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("unavailable deterministic quota admission emits no envelope or business writes", async () =>
{
    await using var host = await GatewayFixture.Start(); var before = host.BusinessSnapshot();
    var native = await Pilot.Authorize(host, host.Command(), options, new(503, "UNAVAILABLE", false));
    Pilot.AssertNoEnvelope(native, 1); Pilot.Equal(before, host.BusinessSnapshot());
    Pilot.Equal(0, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

await Run("exhausted deterministic quota admission emits no envelope or financial debit", async () =>
{
    await using var host = await GatewayFixture.Start(); var before = host.BusinessSnapshot();
    var native = await Pilot.Authorize(host, host.Command(), options, new(429, "MONTHLY_QUOTA_EXCEEDED", false));
    Pilot.AssertNoEnvelope(native, 1); Pilot.Equal(before, host.BusinessSnapshot());
    Pilot.Equal(0, host.Database.All<JsonElement>(Pilot.ReplayBucket).Length);
});

var evidence = new
{
    schemaVersion = "fanasa.gateway-usage.shared-test.v1", recordedAt = DateTimeOffset.UtcNow,
    passed = cases.Count(item => item.Passed), failed = cases.Count(item => !item.Passed), cases,
    sourcePins = new { gatewayFixtureSha256 = Pilot.FixtureHash, bundleSha256 = Pilot.ZipHash, pepLuaSha256 = Pilot.PepHash, contextLuaSha256 = Pilot.ContextHash },
    artifacts = new[] { Pilot.AssemblyEvidence(typeof(OrgCommand).Assembly.Location), Pilot.AssemblyEvidence(typeof(PolicyDecision).Assembly.Location) },
    proof = new { actualPccDecision = true, actualPackagedLuaPep = true, actualPackagedLuaSigner = true, actualKestrelTls = true, actualOrganizationUsage = true,
        admissionMode = "deterministic-test-double", nativeApisix = false, nativeRedis = false, realIdentityProvider = false,
        commercialAcceptance = false, deploymentAcceptance = false,
        quotaAdmission = "admission:<native g-nonce>", financialAdmission = "ledger-credit:Hash(Usage.BusinessKey)",
        quotaDebitIdempotencyProven = false, automaticQuotaRefund = false }
};
var evidencePath = Path.GetFullPath(options.Evidence);
Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Shared Gateway/Usage integration: {evidence.passed} passed, {evidence.failed} failed.");
Environment.ExitCode = evidence.failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> body)
{
    try { await body(); cases.Add(new(name, true, null)); Console.WriteLine("PASS Shared " + name); }
    catch (Exception error) { cases.Add(new(name, false, error.ToString())); Console.Error.WriteLine("FAIL Shared " + name + ": " + error); }
}

sealed record PilotCase(string Name, bool Passed, string? Error);
sealed record QuotaResult(int Status, string Code, bool Allowed);
sealed record PilotOptions(string Python, string Bridge, string Bundle, string Evidence)
{
    public static PilotOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !new[] { "--python", "--bridge", "--bundle", "--evidence" }.Contains(arguments[index])
                || !values.TryAdd(arguments[index], arguments[index + 1])) throw new ArgumentException("Expected unique --python, --bridge, --bundle and --evidence pairs.");
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing " + key);
        return new(Required("--python"), Path.GetFullPath(Required("--bridge")), Path.GetFullPath(Required("--bundle")), Path.GetFullPath(Required("--evidence")));
    }
}

sealed record NativeOutcome(JsonObject Output, string Path, string Body, string Token, string Idempotency)
{
    public bool Allowed => Output["allowed"]!.GetValue<bool>();
    public string Nonce => Output["authorizedContext"]!["requestId"]!.GetValue<string>();
    public SignedRequest Witness()
    {
        if (!Allowed) throw new InvalidOperationException("Denied Lua authorization cannot produce a request witness.");
        return new(Path, Body, Token, Output["context"]!.GetValue<string>(), Output["signature"]!.GetValue<string>(), Idempotency);
    }
}

static class Pilot
{
    internal const string FixtureHash = "0979c82022a5f7e91d743576e23d0dcb84831eee0756984cee5c4ab7b77e3377";
    internal const string ZipHash = "7ef61a999a580bb0f69d8b8d145139708bb2b2175a40244b7930ceef220eec1a";
    internal const string PepHash = "abee6b0932da93530cbfe681a21670b00aa5f49ffd1c997353c637e7b89f121d";
    internal const string ContextHash = "30f3c3f6a29766e6e88ea3d597b9c19a96e61f84d9f9f7619dff60a27d921dc3";
    internal const string ReplayBucket = "contract-policy.gateway-context.replay.v1";
    private const string SyntheticSecret = "synthetic-context-secret-at-least-32-bytes-local-only";

    internal static async Task<NativeOutcome> Authorize(GatewayFixture host, OrgCommand command, PilotOptions options, QuotaResult? quota = null)
    {
        var token = host.Token(); var claims = JsonNode.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]))!.AsObject();
        // The embedded PEP receives the synthetic authentication adapter's identity;
        // PCC independently verifies the untouched original signed bearer on execution.
        claims["active"] = true; claims["client_id"] = claims["azp"]!.DeepClone(); claims["consumerTenantId"] = host.Tenant.ToString("D");
        var nonce = "g-" + Guid.NewGuid().ToString("N");
        var request = JsonNode.Parse(host.PolicyRequest(nonce: nonce))!.AsObject();
        request["tokenExpiresAt"] = claims["exp"]!.DeepClone();
        var now = decimal.Truncate(request["requestedAt"]!.GetValue<decimal>() * 1000m) / 1000m;
        request["requestedAt"] = now;
        var before = host.BusinessSnapshot();
        using var response = await host.Decide(body: request.ToJsonString());
        Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Equal(before, host.BusinessSnapshot());
        var conf = new JsonObject();
        foreach (var name in new[] { "productId", "ownerTenantId", "environment", "action", "operationGroup", "audience", "allocationId", "allocationScope", "scope" })
            conf[name] = request[name]!.DeepClone();
        conf["issuer"] = claims["iss"]!.DeepClone(); conf["allowedClients"] = new JsonArray(claims["client_id"]!.DeepClone());
        var body = JsonSerializer.Serialize(command, ContractJson.Options);
        var path = $"/api/organization-operations/tenants/{host.Tenant:D}/unit.save";
        var admitted = quota ?? new(200, "OK", true);
        var input = new JsonObject
        {
            ["operation"] = "authorizeAndSign", ["conf"] = conf, ["claims"] = claims, ["requestId"] = nonce,
            ["expectedPdpRequest"] = request.DeepClone(), ["decision"] = decision,
            ["admission"] = new JsonObject { ["status"] = admitted.Status, ["code"] = admitted.Code, ["allowed"] = admitted.Allowed },
            ["method"] = "POST", ["rawTarget"] = path, ["authorization"] = "Bearer " + token,
            ["bodyBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), ["idempotencyKey"] = command.RequestId,
            ["secret"] = SyntheticSecret, ["keyId"] = "synthetic-context-key", ["tier"] = "edge", ["nowEpochSeconds"] = now
        };
        var output = await InvokeBridge(options, input);
        var provenance = output["provenance"]!.AsObject();
        Equal(ZipHash, provenance["zipSha256"]!.GetValue<string>());
        Equal(PepHash, provenance["pepLuaSha256"]!.GetValue<string>());
        Equal(ContextHash, provenance["contextLuaSha256"]!.GetValue<string>());
        Equal("packaged-pep-and-context", provenance["mode"]!.GetValue<string>());
        Equal(false, provenance["nativeApisix"]!.GetValue<bool>()); Equal(false, provenance["nativeRedis"]!.GetValue<bool>());
        Equal("deterministic-test-double", provenance["admissionMode"]!.GetValue<string>());
        Equal(1, output["pdpCalls"]!.GetValue<int>());
        Check(JsonNode.DeepEquals(request, output["pdpRequest"]), "Packaged PEP request differs from actual authenticated PCC request.");
        Equal(before, host.BusinessSnapshot());
        return new(output, path, body, token, command.RequestId ?? throw new InvalidOperationException("Business UUID missing."));
    }

    private static async Task<JsonObject> InvokeBridge(PilotOptions options, JsonObject input)
    {
        var start = new ProcessStartInfo(options.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(options.Bridge); start.ArgumentList.Add("--bundle"); start.ArgumentList.Add(options.Bundle);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Lua bridge did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input.ToJsonString()); process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Lua bridge timed out."); }
        var output = await stdout; _ = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Lua bridge failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "; inputs are withheld.");
        return JsonNode.Parse(output)?.AsObject() ?? throw new InvalidOperationException("Lua bridge did not return JSON.");
    }

    internal static void AssertAuthorized(NativeOutcome native)
    {
        Check(native.Allowed, "Packaged PEP denied valid local authorization.");
        Equal(1, native.Output["admissionCalls"]!.GetValue<int>());
        var context = native.Output["authorizedContext"]!.AsObject();
        Equal(native.Nonce, context["decisionId"]!.GetValue<string>());
        Equal("admission:" + native.Nonce, context["admissionId"]!.GetValue<string>());
        Equal(true, context["allowed"]!.GetValue<bool>()); Equal(true, context["confirmed"]!.GetValue<bool>());
        using var envelope = JsonDocument.Parse(Convert.FromBase64String(native.Output["context"]!.GetValue<string>()));
        Check(JsonNode.DeepEquals(context, JsonNode.Parse(envelope.RootElement.GetProperty("context").GetRawText())), "Signer rebuilt a different authorized context.");
    }

    internal static void AssertNoEnvelope(NativeOutcome native, int expectedAdmissionCalls)
    {
        Check(!native.Allowed, "Closed PEP branch allowed execution.");
        Equal(expectedAdmissionCalls, native.Output["admissionCalls"]!.GetValue<int>());
        Check(native.Output["context"] is null && native.Output["signature"] is null && native.Output["authorizedContext"] is null, "Denied PEP emitted executable evidence.");
    }

    internal static void AssertCommitted(GatewayFixture host, OrgCommand command, NativeOutcome native, int outboxBefore)
    {
        Equal(1L, host.Organization.Read(host.Tenant).Revision); Equal(12m, host.Billing.Read(host.Tenant).Outstanding);
        Equal(1, host.Database.All<JsonElement>("organization.history").Length); Equal(1, host.Database.All<JsonElement>("organization.receipt").Length);
        var operation = host.Database.All<MeasuredUsageOperation>("measured.operation").Single();
        var receipt = host.Database.All<MeasuredEventReceipt>("measured.event").Single();
        var usage = UsageEventV1.FromJson(operation.EventJson);
        Equal("committed", operation.Status); Equal(12m, operation.Amount); Check(operation.Error is null, "Committed Usage retains an error.");
        Equal(usage.BusinessKey, operation.BusinessKey); Equal(usage.OperationFingerprint, operation.Fingerprint);
        Equal(usage.BusinessKey, receipt.BusinessKey); Equal(usage.EventFingerprint, receipt.Fingerprint);
        Equal(operation.EventJson, receipt.EventJson);
        using var document = JsonDocument.Parse(operation.EventJson); var wire = document.RootElement;
        Equal(host.Tenant.ToString("D"), Text(wire, "consumerTenantId")); Equal(host.Event.Projection.OwnerTenantId, Text(wire, "ownerTenantId"));
        Equal(host.Event.Projection.ContractId, Text(wire, "contractId"));
        Equal(Convert.ToInt64(host.Event.Projection.ContractVersion, CultureInfo.InvariantCulture), At(wire, "contractVersion").GetInt64());
        Equal(host.Event.Projection.Limits.AllocationId, Text(wire, "allocationId"));
        Equal(native.Output["authorizedContext"]!["allocationPeriodId"]!.GetValue<string>(), Text(wire, "allocationPeriodId"));
        Equal("organization-fabric", Text(wire, "productKey")); Equal("production", Text(wire, "environment"));
        Equal(host.Actor, Text(wire, "operation", "actorId")); Equal(command.RequestId, Text(wire, "operation", "requestId"));
        Equal("organization-unit-save:" + Guid.Parse(command.RequestId!).ToString("D"), Text(wire, "operation", "id"));
        Equal(UsageEventV1.Hash(JsonSerializer.Serialize(command with { ExpectedRevision = 0 })), Text(wire, "operation", "requestFingerprint"));
        Equal("organization-unit-committed/v1", Text(wire, "operation", "billableBoundary"));
        Equal("ledger-credit:" + UsageEventV1.Hash(usage.BusinessKey), Text(wire, "authorization", "admissionId"));
        Check(Text(wire, "authorization", "admissionId") != native.Output["authorizedContext"]!["admissionId"]!.GetValue<string>(), "Quota admission was confused with financial admission.");
        Equal(host.Event.Projection.PricingReference.PriceId, Text(wire, "rateSnapshot", "rateId"));
        Equal(Convert.ToInt64(host.Event.Projection.PricingReference.PriceVersion, CultureInfo.InvariantCulture), At(wire, "rateSnapshot", "rateVersion").GetInt64());
        Equal(12m, At(wire, "rateSnapshot", "unitPrice").GetDecimal()); Equal("IRR", Text(wire, "rateSnapshot", "currency"));
        Equal("organization:" + host.Tenant.ToString("D") + ":1", Text(wire, "evidence", "businessReceiptId"));
        var outbox = host.Database.Outbox(take: 100); Equal(outboxBefore + 3, outbox.Length);
        var committed = outbox.Single(item => item.Kind == "UsageCommitted"); Equal(usage.BusinessKey + ":committed", committed.Key);
        using var payload = JsonDocument.Parse(committed.Payload);
        var delivered = UsageEventV1.FromJson(At(payload.RootElement, "Usage").GetRawText());
        Equal(usage.EventFingerprint, delivered.EventFingerprint);
        Equal(1, outbox.Count(item => item.Kind == "OrganizationChanged"));
        Check(outbox.Count(item => item.Kind == "AccountingChanged") >= 1, "Accounting outbox missing.");
    }

    private static JsonElement At(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            root = root.EnumerateObject().Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return root;
    }
    private static string Text(JsonElement root, params string[] names) => At(root, names).GetString() ?? throw new InvalidOperationException("Wire text missing.");
    internal static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    internal static object AssemblyEvidence(string path) => new { path = Path.GetFullPath(path), sha256 = HashFile(path) };
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
}
