using System.Text;
using System.Text.Json.Nodes;
using Fanasa.HrReadCandidate;

namespace Fanasa.HrReadPdp.Tests;

public static class WireTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Path = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";

    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        { count++; if (!condition) throw new InvalidOperationException("PDP wire test failed: " + label); }
        var settings = Settings();
        var fixture = Fixture(settings);
        var request = Parse(fixture, settings);
        Check(request is not null && request.Target.Kind == HrReadEndpointKind.Chart && request.ConsumerTenantId == Tenant
            && request.Action == HrReadDesign.Action(HrReadEndpointKind.Chart) && request.Ownership.Kind == "Platform"
            && request.OwnerTenantId is null && request.ValidUntil == Now.AddSeconds(5), "bound platform request");
        Check(request is not null && request.Binding == settings && !ReferenceEquals(request.Binding, settings), "settings snapshot is detached");
        var tenantSettings = settings with { Owner = "44444444-4444-4444-8444-444444444444", OwnerTenantId = "44444444-4444-4444-8444-444444444444" };
        Check(Parse(Fixture(tenantSettings), tenantSettings)?.Ownership.Kind == "Tenant", "tenant ownership and owner UUID agree");
        var history = (JsonObject)fixture.DeepClone(); history["rawTarget"] = Path.Replace("/chart", "/history/9223372036854775807");
        history["action"] = HrReadDesign.Action(HrReadEndpointKind.History);
        Check(Parse(history, settings)?.Target.Revision == long.MaxValue, "history target/action use canonical Int64 revision");
        history["action"] = HrReadDesign.Action(HrReadEndpointKind.Chart);
        Check(Parse(history, settings) is null, "chart action never authorizes history path");

        foreach (var key in fixture.Select(property => property.Key).ToArray())
        {
            var changed = (JsonObject)fixture.DeepClone(); changed.Remove(key);
            Check(Parse(changed, settings) is null, "required root field: " + key);
        }
        foreach (var key in ((JsonObject)fixture["ownership"]!).Select(property => property.Key).ToArray())
        {
            var changed = (JsonObject)fixture.DeepClone(); ((JsonObject)changed["ownership"]!).Remove(key);
            Check(Parse(changed, settings) is null, "required ownership field: " + key);
        }
        foreach (var key in new[] { "transportVersion", "schemaVersion", "profileId", "method", "rawTarget", "requestId",
            "subjectId", "clientId", "audience", "scope", "tokenSha256", "consumerTenantId", "productId", "environment",
            "zone", "contractId", "policyId", "allocationId", "allocationScope", "action", "operationGroup" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed[key] = "wrong";
            Check(Parse(changed, settings) is null, "mismatched bound string: " + key);
        }
        foreach (var key in new[] { "contractVersion", "policyVersion", "configRevision" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed[key] = 2;
            Check(Parse(changed, settings) is null, "independent version mismatch: " + key);
        }
        foreach (var key in new[] { "kind", "owner", "registeredProductId" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["ownership"]![key] = "wrong";
            Check(Parse(changed, settings) is null, "ownership mismatch: " + key);
        }
        foreach (var key in new[] { "grant", "authenticationValid", "clientAuthorized", "decision", "rawJwt", "issuer" })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed[key] = true;
            Check(Parse(changed, settings) is null, "caller PIP/token/unknown field refused: " + key);
        }
        var extraOwner = (JsonObject)fixture.DeepClone(); extraOwner["ownership"]!["unknown"] = true;
        Check(Parse(extraOwner, settings) is null, "unknown ownership field refused");
        var fakeOwner = (JsonObject)fixture.DeepClone(); fakeOwner["ownerTenantId"] = Tenant;
        Check(Parse(fakeOwner, settings) is null, "platform cannot synthesize owner tenant");
        var arrayConsumer = (JsonObject)fixture.DeepClone(); arrayConsumer["consumerTenantId"] = new JsonArray(Tenant);
        Check(Parse(arrayConsumer, settings) is null, "consumer must be a scalar");
        var arrayOwnership = (JsonObject)fixture.DeepClone(); arrayOwnership["ownership"] = new JsonArray();
        Check(Parse(arrayOwnership, settings) is null, "ownership must be an object");
        var raw = fixture.ToJsonString();
        foreach (var malformed in new[]
        {
            raw.Replace("\"scope\":\"organization.hr.read\"", "\"scope\":\"organization.hr.read\",\"scope\":\"organization.hr.read\"", StringComparison.Ordinal),
            raw.Replace("\"scope\":\"organization.hr.read\"", "\"scope\":\"organization.hr.read\",\"sc\\u006fpe\":\"organization.hr.read\"", StringComparison.Ordinal),
            raw.Replace("\"kind\":\"Platform\"", "\"kind\":\"Platform\",\"kind\":\"Platform\"", StringComparison.Ordinal),
            raw.Replace("\"configRevision\":1", "\"configRevision\":1.0", StringComparison.Ordinal),
            raw.Replace("\"configRevision\":1", "\"configRevision\":1e0", StringComparison.Ordinal),
            raw.Replace("\"subjectId\":", "\"SubjectId\":", StringComparison.Ordinal)
        }) Check(HrReadPdpWire.Parse(Encoding.UTF8.GetBytes(malformed), settings, Now) is null, "strict duplicate/case/numeric JSON");
        Check(HrReadPdpWire.Parse(new byte[] { 0xff }, settings, Now) is null, "invalid UTF8 rejected");
        Check(HrReadPdpWire.Parse(ReadOnlyMemory<byte>.Empty, settings, Now) is null, "empty body rejected");
        Check(HrReadPdpWire.Parse(new byte[HrReadPdpWire.MaximumRequestBytes + 1], settings, Now) is null, "byte allocation cap enforced");
        Check(HrReadPdpWire.Parse(Encoding.UTF8.GetBytes(new string('[', 10) + "0" + new string(']', 10)), settings, Now) is null, "excessive JSON depth rejected");
        Check(HrReadPdpWire.Parse(Encoding.UTF8.GetBytes("null"), settings, Now) is null, "null root rejected");

        var future = (JsonObject)fixture.DeepClone(); future["requestedAt"] = Now.ToUnixTimeSeconds() + 0.0000001m;
        Check(Parse(future, settings) is null, "future timestamp refused at tick precision");
        var edge = (JsonObject)fixture.DeepClone(); edge["requestedAt"] = Now.ToUnixTimeSeconds() - 5;
        Check(Parse(edge, settings) is null, "five-second freshness deadline is exclusive");
        var fractional = (JsonObject)fixture.DeepClone(); fractional["requestedAt"] = Now.ToUnixTimeSeconds() - 0.25m;
        Check(Parse(fractional, settings)?.ValidUntil == Now.AddSeconds(4.75), "fractional requestedAt retains tick precision");
        var expiredToken = (JsonObject)fixture.DeepClone(); expiredToken["tokenExpiresAt"] = Now.ToUnixTimeSeconds();
        Check(Parse(expiredToken, settings) is null, "asserted token expiry is exclusive");
        var shortToken = (JsonObject)fixture.DeepClone(); shortToken["tokenExpiresAt"] = Now.ToUnixTimeSeconds() + 1;
        Check(Parse(shortToken, settings)?.ValidUntil == Now.AddSeconds(1), "token assertion shortens transport ceiling");
        var upperHash = (JsonObject)fixture.DeepClone(); upperHash["tokenSha256"] = new string('A', 64);
        Check(Parse(upperHash, settings) is null, "token hash is lowercase canonical SHA256");
        foreach (var path in new[] { Path + "?x=1", Path + "/", Path.Replace("/chart", "/commands"),
            Path.Replace("/chart", "/history/01") })
        {
            var changed = (JsonObject)fixture.DeepClone(); changed["rawTarget"] = path;
            Check(Parse(changed, settings) is null, "canonical GET target required");
        }
        Check(Parse(fixture, settings with { Enabled = false }) is null, "disabled configuration closes");
        Check(Parse(fixture, settings with { SchemaVersion = "4.0.0" }) is null, "unknown configured version closes");
        Check(Parse(fixture, settings with { MaximumWitnessSeconds = 6 }) is null, "configuration cannot widen five-second ceiling");
        var bytes = Encoding.UTF8.GetBytes(raw);
        var retained = HrReadPdpWire.Parse(bytes, settings, Now);
        Array.Fill<byte>(bytes, 0);
        Check(retained?.RawTarget == Path && retained.SubjectId == settings.SubjectId, "parsed model retains no mutable request memory");
        return count;
    }

    // Synthetic fixture identifiers only. A parsed request is never an authenticated subject or a live grant.
    public static HrReadReceiverSettings Settings() => new()
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
    public static JsonObject Fixture(HrReadReceiverSettings settings) => new()
    {
        ["transportVersion"] = HrReadPdpWire.RequestVersion, ["schemaVersion"] = HrReadDesign.SchemaVersion,
        ["profileId"] = HrReadDesign.ProfileId, ["method"] = "GET", ["rawTarget"] = Path,
        ["requestId"] = "g-0123456789abcdef0123456789abcdef", ["requestedAt"] = Now.ToUnixTimeSeconds(),
        ["subjectId"] = settings.SubjectId, ["clientId"] = settings.ClientId, ["audience"] = settings.Audience,
        ["scope"] = HrReadDesign.ReadScope, ["tokenSha256"] = new string('a', 64), ["tokenExpiresAt"] = Now.ToUnixTimeSeconds() + 60,
        ["consumerTenantId"] = settings.ConsumerTenantId, ["ownerTenantId"] = settings.OwnerTenantId,
        ["ownership"] = new JsonObject { ["kind"] = settings.Owner == "platform" ? "Platform" : "Tenant", ["owner"] = settings.Owner, ["registeredProductId"] = settings.RegisteredProductId },
        ["productId"] = settings.ProductKey, ["environment"] = settings.Environment, ["zone"] = settings.Zone,
        ["contractId"] = settings.ContractId, ["contractVersion"] = settings.ContractVersion,
        ["policyId"] = settings.PolicyId, ["policyVersion"] = settings.PolicyVersion,
        ["allocationId"] = settings.AllocationId, ["allocationScope"] = settings.CanonicalScope,
        ["configRevision"] = settings.ConfigRevision, ["action"] = HrReadDesign.Action(HrReadEndpointKind.Chart), ["operationGroup"] = HrReadDesign.Group
    };
    private static HrReadPdpRequest? Parse(JsonObject json, HrReadReceiverSettings settings)
        => HrReadPdpWire.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), settings, Now);
}
