using System.Globalization;
using System.Text;
using System.Text.Json;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;

namespace Fanasa.HrReadPdp;

/// <summary>
/// A parsed, structurally bound request from a caller. Identity, token hash, deadline and
/// tuple values remain assertions until the separately authenticated Gateway delegation,
/// subject evidence and current authoritative store have all been verified by the host.
/// This object never contains a caller-supplied grant, PIP context, Permit or raw JWT.
/// </summary>
public sealed class HrReadPdpRequest
{
    internal HrReadPdpRequest(HrReadReceiverSettings binding, HrReadTarget target, string rawTarget,
        string requestId, decimal requestedAt, string tokenSha256, long tokenExpiresAt)
    {
        Binding = binding with { }; Target = target; RawTarget = rawTarget;
        RequestId = requestId; RequestedAt = requestedAt; TokenSha256 = tokenSha256; TokenExpiresAt = tokenExpiresAt;
        Ownership = new(binding.Owner == "platform" ? "Platform" : "Tenant", binding.Owner, binding.RegisteredProductId);
        var deadline = Math.Min(requestedAt + binding.MaximumWitnessSeconds, tokenExpiresAt);
        ValidUntil = DateTimeOffset.UnixEpoch.AddTicks(decimal.ToInt64(deadline * TimeSpan.TicksPerSecond));
    }
    public HrReadReceiverSettings Binding { get; }
    public HrReadTarget Target { get; }
    public string TransportVersion => HrReadPdpWire.RequestVersion;
    public string SchemaVersion => HrReadDesign.SchemaVersion;
    public string ProfileId => HrReadDesign.ProfileId;
    public string Method => "GET";
    public string RawTarget { get; }
    public string RequestId { get; }
    public decimal RequestedAt { get; }
    public string SubjectId => Binding.SubjectId;
    public string ClientId => Binding.ClientId;
    public string Audience => Binding.Audience;
    public string Scope => HrReadDesign.ReadScope;
    public string TokenSha256 { get; }
    public long TokenExpiresAt { get; }
    public string ConsumerTenantId => Binding.ConsumerTenantId;
    public string? OwnerTenantId => Binding.OwnerTenantId;
    public ProductOwnershipReference Ownership { get; }
    public string ProductId => Binding.ProductKey;
    public string Environment => Binding.Environment;
    public string Zone => Binding.Zone;
    public string Action => HrReadDesign.Action(Target.Kind);
    public string OperationGroup => HrReadDesign.Group;
    public string ContractId => Binding.ContractId;
    public long ContractVersion => Binding.ContractVersion;
    public string PolicyId => Binding.PolicyId;
    public long PolicyVersion => Binding.PolicyVersion;
    public string AllocationId => Binding.AllocationId;
    public string AllocationScope => Binding.CanonicalScope;
    public long ConfigRevision => Binding.ConfigRevision;
    /// <summary>Exclusive asserted transport/token ceiling. Authentication and authoritative evidence may shorten it.</summary>
    public DateTimeOffset ValidUntil { get; }
}

/// <summary>Strict bounded transport parsing only; this API has no authority or persistence side effects.</summary>
public static class HrReadPdpWire
{
    public const string RequestVersion = "fanasa.hr-read.pdp.request/1";
    public const string ResponseVersion = "fanasa.hr-read.pdp.response/1";
    public const string EndpointPath = "/internal/contract-policy/hr-read/v3/evaluate";
    public const int MaximumRequestBytes = 32768;
    private const long MaximumSafeInteger = 9007199254740991L;
    private const long MaximumEpochSeconds = 253402300799L;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RequiredKeys = ["transportVersion", "schemaVersion", "profileId", "method", "rawTarget",
        "requestId", "requestedAt", "subjectId", "clientId", "audience", "scope", "tokenSha256", "tokenExpiresAt",
        "consumerTenantId", "ownerTenantId", "ownership", "productId", "environment", "zone", "contractId",
        "contractVersion", "policyId", "policyVersion", "allocationId", "allocationScope", "configRevision", "action", "operationGroup"];
    private static readonly string[] OwnershipKeys = ["kind", "owner", "registeredProductId"];

    public static HrReadPdpRequest? Parse(ReadOnlyMemory<byte> json, HrReadReceiverSettings settings, DateTimeOffset now)
    {
        try
        {
            if (json.Length is 0 or > MaximumRequestBytes || settings is null || !settings.Enabled
                || settings.Validate().Count != 0) return null;
            // ReadOnlyMemory can wrap a caller-owned mutable byte array; retain no parser view over it.
            var owned = json.ToArray();
            _ = StrictUtf8.GetString(owned);
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            RejectDuplicates(root);
            if (!OnlyKeys(root, RequiredKeys)
                || !EqualsText(root, "transportVersion", RequestVersion)
                || !EqualsText(root, "schemaVersion", HrReadDesign.SchemaVersion)
                || !EqualsText(root, "profileId", HrReadDesign.ProfileId)
                || !Text(root, "method", out var method) || !Text(root, "rawTarget", out var rawTarget)
                || !HrReadWire.TryParseTarget(method, rawTarget, out var target) || target is null
                || target.TenantId.ToString("D", CultureInfo.InvariantCulture) != settings.ConsumerTenantId
                || !EqualsText(root, "action", HrReadDesign.Action(target.Kind))
                || !EqualsText(root, "operationGroup", HrReadDesign.Group)
                || !Text(root, "requestId", out var requestId) || requestId.Length != 34
                || !requestId.StartsWith("g-", StringComparison.Ordinal) || !LowerHex(requestId[2..], 32)
                || !Seconds(root.GetProperty("requestedAt"), out var requestedAt)
                || !Text(root, "tokenSha256", out var tokenHash) || !LowerHex(tokenHash, 64)
                || !Integer(root.GetProperty("tokenExpiresAt"), 1, MaximumEpochSeconds, out var tokenExpiresAt)) return null;
            var instant = Epoch(now);
            if (requestedAt > instant || instant >= requestedAt + settings.MaximumWitnessSeconds
                || instant >= tokenExpiresAt) return null;

            if (!EqualsText(root, "subjectId", settings.SubjectId) || !EqualsText(root, "clientId", settings.ClientId)
                || !EqualsText(root, "audience", settings.Audience) || !EqualsText(root, "scope", HrReadDesign.ReadScope)
                || !EqualsText(root, "consumerTenantId", settings.ConsumerTenantId)
                || !EqualsNullableText(root.GetProperty("ownerTenantId"), settings.OwnerTenantId)
                || !EqualsText(root, "productId", settings.ProductKey)
                || !EqualsText(root, "environment", settings.Environment) || !EqualsText(root, "zone", settings.Zone)
                || !EqualsText(root, "contractId", settings.ContractId) || !IntegerEquals(root, "contractVersion", settings.ContractVersion)
                || !EqualsText(root, "policyId", settings.PolicyId) || !IntegerEquals(root, "policyVersion", settings.PolicyVersion)
                || !EqualsText(root, "allocationId", settings.AllocationId) || !EqualsText(root, "allocationScope", settings.CanonicalScope)
                || !IntegerEquals(root, "configRevision", settings.ConfigRevision)) return null;
            var ownership = root.GetProperty("ownership");
            if (!OnlyKeys(ownership, OwnershipKeys)
                || !EqualsText(ownership, "kind", settings.Owner == "platform" ? "Platform" : "Tenant")
                || !EqualsText(ownership, "owner", settings.Owner)
                || !EqualsText(ownership, "registeredProductId", settings.RegisteredProductId)) return null;
            return new(settings, target, rawTarget, requestId, requestedAt, tokenHash, tokenExpiresAt);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return null; }
    }

    private static bool Text(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text) return false;
        value = text; return true;
    }
    private static bool EqualsText(JsonElement parent, string name, string expected)
        => Text(parent, name, out var value) && value == expected;
    private static bool EqualsNullableText(JsonElement value, string? expected)
        => expected is null ? value.ValueKind == JsonValueKind.Null : value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool LowerHex(string? value, int length) => value?.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IntegerEquals(JsonElement parent, string name, long expected)
        => Integer(parent.GetProperty(name), 1, MaximumSafeInteger, out var value) && value == expected;
    private static bool Integer(JsonElement value, long minimum, long maximum, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number) && number >= minimum && number <= maximum
            && value.GetRawText() == number.ToString(CultureInfo.InvariantCulture);
    }
    private static bool Seconds(JsonElement value, out decimal number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number)
            && number is > 0 and <= MaximumEpochSeconds
            && value.GetRawText() == number.ToString("0.#######", CultureInfo.InvariantCulture);
    }
    private static bool OnlyKeys(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) return false;
        return keys.Count == expected.Length;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate PDP request property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static decimal Epoch(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (decimal)TimeSpan.TicksPerSecond;
}
