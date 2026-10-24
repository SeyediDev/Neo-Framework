using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;

namespace Fanasa.HrReadCandidate.Http.Tests;

public static class EnvelopeTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("fixture-envelope-secret-32-bytes-only");
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Authorization = "Bearer fixture.header.signature";
    private const string Nonce = "g-0123456789abcdef0123456789abcdef";
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Chart = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";

    public static int Run()
    {
        var assertions = 0;
        void Check(bool passed, string label)
        { assertions++; if (!passed) throw new InvalidOperationException("Envelope test failed: " + label); }
        var settings = Settings();
        var fixture = Fixture(settings);
        var accepted = Verify(fixture, settings);
        Check(accepted is not null && accepted.RequestId == Nonce && accepted.Target.Kind == HrReadEndpointKind.Chart
            && accepted.Action == HrReadDesign.Action(HrReadEndpointKind.Chart)
            && accepted.Ownership.Kind == "Platform" && accepted.OwnerTenantId is null
            && accepted.ValidUntil == Now.AddSeconds(3) && accepted.TokenExpiresAt == Now.ToUnixTimeSeconds() + 60,
            "fully matched signed platform envelope");
        Check(accepted is not null && accepted.Authorization == Authorization && accepted.RawTarget == Chart
            && accepted.EncodedEnvelope == Encode(fixture) && accepted.Binding == settings, "exact immutable wire and copied binding");
        Check(Verify(fixture, settings, "fixture-id") is not null, "optional idempotency bytes are signed");
        var tenantOwned = settings with { Owner = "44444444-4444-4444-8444-444444444444", OwnerTenantId = "44444444-4444-4444-8444-444444444444" };
        Check(Verify(Fixture(tenantOwned), tenantOwned)?.Ownership.Kind == "Tenant", "tenant ownership agrees with same UUID");
        var history = Fixture(settings);
        var historyPath = Chart.Replace("/chart", "/history/9223372036854775807");
        history["context"]!["rawTarget"] = historyPath;
        history["context"]!["action"] = HrReadDesign.Action(HrReadEndpointKind.History);
        Check(Verify(history, settings, rawTarget: historyPath)?.Target.Revision == long.MaxValue, "history action binds canonical Int64 target");

        foreach (var key in fixture.Select(property => property.Key).ToArray())
        {
            var changed = (JsonObject)fixture.DeepClone(); changed.Remove(key);
            Check(Verify(changed, settings) is null, "every root field is required: " + key);
        }
        foreach (var key in ((JsonObject)fixture["context"]!).Select(property => property.Key).ToArray())
        {
            var changed = (JsonObject)fixture.DeepClone(); ((JsonObject)changed["context"]!).Remove(key);
            Check(Verify(changed, settings) is null, "every context field is required: " + key);
        }
        foreach (var key in ((JsonObject)fixture["context"]!["ownership"]!).Select(property => property.Key).ToArray())
        {
            var changed = (JsonObject)fixture.DeepClone(); ((JsonObject)changed["context"]!["ownership"]!).Remove(key);
            Check(Verify(changed, settings) is null, "every ownership field is required: " + key);
        }
        foreach (var key in new[] { "envelopeVersion", "schemaVersion", "profileId", "keyId", "tier", "tokenSha256" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed[key] = "wrong";
            Check(Verify(changed, settings) is null, "signed root mismatch closes: " + key);
        }
        foreach (var key in new[] { "requestId", "decisionId", "admissionId", "method", "rawTarget", "subjectId", "clientId",
            "consumerTenantId", "productId", "audience", "issuer", "scope", "environment", "zone", "action", "operationGroup",
            "contractId", "policyId", "allocationId", "allocationScope", "allocationPeriodId" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["context"]![key] = "wrong";
            Check(Verify(changed, settings) is null, "signed context mismatch closes: " + key);
        }
        foreach (var key in new[] { "contractVersion", "policyVersion", "configRevision" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["context"]![key] = 2;
            Check(Verify(changed, settings) is null, "independent version mismatch closes: " + key);
        }
        foreach (var key in new[] { "allowed", "confirmed" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["context"]![key] = false;
            Check(Verify(changed, settings) is null, "signed veto closes: " + key);
        }
        foreach (var key in new[] { "kind", "owner", "registeredProductId" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["context"]!["ownership"]![key] = "wrong";
            Check(Verify(changed, settings) is null, "ownership mismatch closes: " + key);
        }
        var wrongOwner = (JsonObject)fixture.DeepClone(); wrongOwner["context"]!["ownerTenantId"] = Tenant;
        Check(Verify(wrongOwner, settings) is null, "platform may not manufacture an owner tenant");
        var rootUnknown = (JsonObject)fixture.DeepClone(); rootUnknown["secret"] = "untrusted-key";
        Check(Verify(rootUnknown, settings) is null, "keys cannot arrive in the envelope");
        var contextUnknown = (JsonObject)fixture.DeepClone(); contextUnknown["context"]!["unknown"] = 1;
        Check(Verify(contextUnknown, settings) is null, "unknown context field closes");
        var ownerUnknown = (JsonObject)fixture.DeepClone(); ownerUnknown["context"]!["ownership"]!["unknown"] = 1;
        Check(Verify(ownerUnknown, settings) is null, "unknown ownership field closes");

        var rawJson = fixture.ToJsonString();
        foreach (var bad in new[]
        {
            rawJson.Replace("\"confirmed\":true", "\"confirmed\":true,\"confirmed\":true", StringComparison.Ordinal),
            rawJson.Replace("\"confirmed\":true", "\"confirmed\":true,\"confi\\u0072med\":true", StringComparison.Ordinal),
            rawJson.Replace("\"kind\":\"Platform\"", "\"kind\":\"Platform\",\"kind\":\"Platform\"", StringComparison.Ordinal),
            rawJson.Replace("\"contractVersion\":1", "\"contractVersion\":1.0", StringComparison.Ordinal),
            rawJson.Replace("\"contractVersion\":1", "\"contractVersion\":1e0", StringComparison.Ordinal),
            rawJson.Replace("\"subjectId\":", "\"SubjectId\":", StringComparison.Ordinal),
            rawJson[..^1] + ",\"context\":{} }"
        }) Check(VerifyRaw(Encoding.UTF8.GetBytes(bad), settings) is null, "duplicate/unknown/noncanonical numeric JSON rejected");
        Check(VerifyRaw([0xff], settings) is null, "invalid UTF8 rejected");
        Check(VerifyRaw(new byte[HrReadEnvelopeVerifier.MaximumDecodedEnvelopeBytes + 1], settings) is null, "decoded envelope byte cap enforced before parsing");
        Check(VerifyRaw(Encoding.UTF8.GetBytes(new string('[', 10) + "0" + new string(']', 10)), settings) is null, "excessive JSON depth rejected");
        Check(VerifyRaw(Encoding.UTF8.GetBytes("null"), settings) is null, "null root rejected");
        var arrayConsumer = (JsonObject)fixture.DeepClone(); arrayConsumer["context"]!["consumerTenantId"] = new JsonArray(Tenant);
        Check(Verify(arrayConsumer, settings) is null, "consumer is scalar, never one-element array");
        var arrayOwner = (JsonObject)fixture.DeepClone(); arrayOwner["context"]!["ownership"] = new JsonArray();
        Check(Verify(arrayOwner, settings) is null, "ownership object required");

        var future = (JsonObject)fixture.DeepClone(); future["issuedAt"] = Now.ToUnixTimeSeconds() + 1;
        Check(Verify(future, settings) is null, "future issuance rejected");
        var expired = (JsonObject)fixture.DeepClone(); expired["issuedAt"] = Now.ToUnixTimeSeconds() - 4; expired["expiresAt"] = Now.ToUnixTimeSeconds();
        Check(Verify(expired, settings) is null, "expiry is exclusive");
        var longWitness = (JsonObject)fixture.DeepClone(); longWitness["expiresAt"] = Now.ToUnixTimeSeconds() + 6;
        Check(Verify(longWitness, settings) is null, "witness lifetime capped at five seconds");
        var longDecision = (JsonObject)fixture.DeepClone(); longDecision["context"]!["decisionValidUntil"] = Now.ToUnixTimeSeconds() + 6;
        Check(Verify(longDecision, settings) is null, "decision deadline capped by read profile");
        var tokenBeforeWitness = (JsonObject)fixture.DeepClone(); tokenBeforeWitness["context"]!["tokenExpiresAt"] = Now.ToUnixTimeSeconds() + 2;
        Check(Verify(tokenBeforeWitness, settings) is null, "witness cannot outlive token");
        var decisionBeforeWitness = (JsonObject)fixture.DeepClone(); decisionBeforeWitness["context"]!["decisionValidUntil"] = Now.ToUnixTimeSeconds() + 2;
        Check(Verify(decisionBeforeWitness, settings) is null, "witness cannot outlive decision");
        var wrongPeriod = (JsonObject)fixture.DeepClone(); wrongPeriod["context"]!["periodEndMs"] = 1;
        Check(Verify(wrongPeriod, settings) is null, "calendar period boundaries verified");
        var fraction = (JsonObject)fixture.DeepClone(); fraction["issuedAt"] = Now.ToUnixTimeSeconds() - 0.25m;
        fraction["expiresAt"] = Now.ToUnixTimeSeconds() + 2.5m;
        fraction["context"]!["decisionValidUntil"] = Now.ToUnixTimeSeconds() + 4.5m;
        Check(Verify(fraction, settings)?.ValidUntil == Now.AddSeconds(2.5), "exact fractional epoch at tick precision");

        var encoded = Encode(fixture); var signature = Sign(encoded, Chart, Authorization, "");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", encoded + "\n", signature, Key, settings, Now) is null,
            "noncanonical Base64 rejected");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", new string('A', HrReadWire.MaximumEnvelopeBytes + 1), signature, Key, settings, Now) is null,
            "encoded envelope allocation cap enforced");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", encoded, signature.ToUpperInvariant(), Key, settings, Now) is null,
            "signature must be canonical lowercase hex");
        var wrongKey = Key.ToArray(); wrongKey[0] ^= 1;
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", encoded, signature, wrongKey, settings, Now) is null, "wrong trusted key rejected");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", encoded, signature, new byte[31], settings, Now) is null, "undersized key rejected");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, "bearer fixture.header.signature", "", encoded, signature, Key, settings, Now) is null,
            "same token with altered exact header invalidates signature");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "new-id", encoded, signature, Key, settings, Now) is null,
            "changed optional header invalidates signature");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, new string('a', 129), encoded, signature, Key, settings, Now) is null,
            "optional idempotency header size cap enforced");
        Check(HrReadEnvelopeVerifier.Verify("GET", Chart, Authorization, "", encoded, signature, Key, settings, Now.AddSeconds(3)) is null,
            "exact envelope deadline is exclusive");
        Check(Verify(fixture, settings, "bad,key") is null, "idempotency header alphabet enforced");
        Check(Verify(fixture, settings, authorization: "Bearer  fixture.header.signature") is null, "ambiguous Bearer spacing rejected");
        Check(Verify(fixture, settings, rawTarget: Chart + "?x=1") is null, "query target rejected");
        Check(Verify(fixture, settings with { Enabled = false }) is null, "disabled settings always close");
        Check(Verify(fixture, settings with { MaximumWitnessSeconds = 1 }) is null, "stricter configured lifetime enforced");
        Check(Verify(fixture, settings with { ProfileId = "unknown" }) is null, "unknown configured profile closes");
        return assertions;
    }

    // All values below are isolated synthetic fixtures, not a deployed grant/allocation/key.
    internal static HrReadReceiverSettings Settings() => new()
    {
        Enabled = true, SchemaVersion = HrReadDesign.SchemaVersion, ProfileId = HrReadDesign.ProfileId,
        RegisteredProductId = "22222222-2222-4222-8222-222222222222", ProductKey = HrReadDesign.ProductKey,
        Owner = "platform", OwnerTenantId = null, ConsumerTenantId = Tenant,
        Issuer = "https://sso.example.test/realms/fanasa", Audience = HrReadDesign.Audience,
        ClientId = "fixture-client", SubjectId = "33333333-3333-4333-8333-333333333333",
        Environment = "development", Zone = "fixture-zone", ContractId = "fixture-contract", ContractVersion = 1,
        PolicyId = "fixture-policy", PolicyVersion = 1, AllocationId = "fixture-allocation", ConfigRevision = 1,
        CanonicalScope = "tenant=" + Tenant + ";products=organization-fabric;environments=development;groups=organization.hr.read",
        KeyId = "fixture-key", Tier = "internal", PeerCertificateSha256 = new string('a', 64),
        MaximumResponseBytes = 1024, MaximumWitnessSeconds = 5
    };

    private static JsonObject Fixture(HrReadReceiverSettings s) => new()
    {
        ["envelopeVersion"] = HrReadEnvelopeVerifier.EnvelopeVersion, ["schemaVersion"] = HrReadDesign.SchemaVersion,
        ["profileId"] = HrReadDesign.ProfileId, ["keyId"] = s.KeyId, ["tier"] = s.Tier,
        ["issuedAt"] = Now.ToUnixTimeSeconds(), ["expiresAt"] = Now.ToUnixTimeSeconds() + 3,
        ["tokenSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Authorization[7..]))).ToLowerInvariant(),
        ["context"] = new JsonObject
        {
            ["requestId"] = Nonce, ["decisionId"] = Nonce, ["admissionId"] = "admission:" + Nonce,
            ["method"] = "GET", ["rawTarget"] = Chart, ["subjectId"] = s.SubjectId, ["clientId"] = s.ClientId,
            ["consumerTenantId"] = s.ConsumerTenantId, ["ownerTenantId"] = s.OwnerTenantId,
            ["ownership"] = new JsonObject { ["kind"] = s.Owner == "platform" ? "Platform" : "Tenant", ["owner"] = s.Owner, ["registeredProductId"] = s.RegisteredProductId },
            ["productId"] = s.ProductKey, ["audience"] = s.Audience, ["issuer"] = s.Issuer, ["scope"] = HrReadDesign.ReadScope,
            ["environment"] = s.Environment, ["zone"] = s.Zone, ["action"] = HrReadDesign.Action(HrReadEndpointKind.Chart),
            ["operationGroup"] = HrReadDesign.Group, ["contractId"] = s.ContractId, ["contractVersion"] = s.ContractVersion,
            ["policyId"] = s.PolicyId, ["policyVersion"] = s.PolicyVersion, ["allocationId"] = s.AllocationId,
            ["allocationScope"] = s.CanonicalScope, ["configRevision"] = s.ConfigRevision, ["allocationPeriodId"] = "2026-10",
            ["periodStartMs"] = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ["periodEndMs"] = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ["decisionValidUntil"] = Now.ToUnixTimeSeconds() + 4, ["tokenExpiresAt"] = Now.ToUnixTimeSeconds() + 60,
            ["allowed"] = true, ["confirmed"] = true
        }
    };
    private static VerifiedHrReadEnvelope? Verify(JsonObject envelope, HrReadReceiverSettings settings, string idempotency = "",
        string rawTarget = Chart, string authorization = Authorization)
        => VerifyRaw(Encoding.UTF8.GetBytes(envelope.ToJsonString()), settings, idempotency, rawTarget, authorization);
    private static VerifiedHrReadEnvelope? VerifyRaw(byte[] json, HrReadReceiverSettings settings, string idempotency = "",
        string rawTarget = Chart, string authorization = Authorization)
    {
        var encoded = Convert.ToBase64String(json);
        return HrReadEnvelopeVerifier.Verify("GET", rawTarget, authorization, idempotency, encoded,
            Sign(encoded, rawTarget, authorization, idempotency), Key, settings, Now);
    }
    private static string Encode(JsonObject envelope) => Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
    private static string Sign(string encoded, string target, string authorization, string idempotency)
        => Convert.ToHexString(HrReadWire.Sign(HrReadWire.BuildFrame("GET", target, authorization, encoded, idempotency, []), Key)).ToLowerInvariant();
}
