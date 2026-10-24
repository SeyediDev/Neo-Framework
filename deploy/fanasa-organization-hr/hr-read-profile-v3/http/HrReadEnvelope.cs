using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fanasa.HrReadCandidate.Http;

public sealed record HrReadEnvelopeOwnership(string Kind, string Owner, string RegisteredProductId);

/// <summary>
/// An immutable proof that the bounded envelope and its exact wire signature passed this verifier.
/// It is NOT an access grant, introspection result, observed debit, current policy or admission.
/// A host must independently authenticate and revalidate all authoritative sources.
/// </summary>
public sealed class VerifiedHrReadEnvelope
{
    internal VerifiedHrReadEnvelope(HrReadReceiverSettings binding, HrReadTarget target,
        HrReadEnvelopeOwnership ownership, string requestId, decimal issuedAt, decimal expiresAt,
        decimal decisionValidUntil, long tokenExpiresAt, string allocationPeriodId,
        long periodStartMs, long periodEndMs, string tokenSha256, string method, string rawTarget,
        string authorization, string idempotencyKey, string encodedEnvelope)
    {
        Binding = binding with { }; Target = target; Ownership = ownership; RequestId = requestId;
        IssuedAt = issuedAt; ExpiresAt = expiresAt; DecisionValidUntil = decisionValidUntil;
        TokenExpiresAt = tokenExpiresAt; AllocationPeriodId = allocationPeriodId;
        PeriodStartMs = periodStartMs; PeriodEndMs = periodEndMs; TokenSha256 = tokenSha256;
        Method = method; RawTarget = rawTarget; Authorization = authorization;
        IdempotencyKey = idempotencyKey; EncodedEnvelope = encodedEnvelope;
        ValidUntil = DateTimeOffset.UnixEpoch.AddTicks(decimal.ToInt64(expiresAt * TimeSpan.TicksPerSecond));
    }

    public HrReadReceiverSettings Binding { get; }
    public HrReadTarget Target { get; }
    public HrReadEnvelopeOwnership Ownership { get; }
    public string EnvelopeVersion => HrReadEnvelopeVerifier.EnvelopeVersion;
    public string SchemaVersion => HrReadDesign.SchemaVersion;
    public string ProfileId => HrReadDesign.ProfileId;
    public string KeyId => Binding.KeyId;
    public string Tier => Binding.Tier;
    public string SubjectId => Binding.SubjectId;
    public string ClientId => Binding.ClientId;
    public string ConsumerTenantId => Binding.ConsumerTenantId;
    public string? OwnerTenantId => Binding.OwnerTenantId;
    public string Action => HrReadDesign.Action(Target.Kind);
    public string RequestId { get; }
    public string DecisionId => RequestId;
    public string AdmissionId => "admission:" + RequestId;
    public decimal IssuedAt { get; }
    public decimal ExpiresAt { get; }
    public decimal DecisionValidUntil { get; }
    public long TokenExpiresAt { get; }
    public string AllocationPeriodId { get; }
    public long PeriodStartMs { get; }
    public long PeriodEndMs { get; }
    /// <summary>Exclusive envelope deadline, already bounded by token, decision and period deadlines.</summary>
    public DateTimeOffset ValidUntil { get; }
    public string TokenSha256 { get; }
    // Exact sensitive wire bytes are kept in memory solely for the host's request binding. Never log this object.
    public string Method { get; }
    public string RawTarget { get; }
    public string Authorization { get; }
    public string IdempotencyKey { get; }
    public string EncodedEnvelope { get; }
}

/// <summary>
/// A new explicitly versioned HR3 envelope, separate from the command receiver's envelope.
/// No key, permission, allocation or admission is created by this verifier. Its caller supplies
/// a key from a trusted host source and must separately prove the actual GET stream is empty.
/// </summary>
public static class HrReadEnvelopeVerifier
{
    public const string EnvelopeVersion = "fanasa.hr-read.context/1";
    public const int MaximumDecodedEnvelopeBytes = 16384;
    private const long MaximumSafeInteger = 9007199254740991L;
    private const decimal MaximumEpochSeconds = 253402300799m;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] EnvelopeKeys = ["envelopeVersion", "schemaVersion", "profileId", "keyId", "tier",
        "issuedAt", "expiresAt", "tokenSha256", "context"];
    private static readonly string[] ContextKeys = ["requestId", "decisionId", "admissionId", "method", "rawTarget",
        "subjectId", "clientId", "consumerTenantId", "ownerTenantId", "ownership", "productId", "audience", "issuer",
        "scope", "environment", "zone", "action", "operationGroup", "contractId", "contractVersion", "policyId",
        "policyVersion", "allocationId", "allocationScope", "configRevision", "allocationPeriodId", "periodStartMs",
        "periodEndMs", "decisionValidUntil", "tokenExpiresAt", "allowed", "confirmed"];
    private static readonly string[] OwnershipKeys = ["kind", "owner", "registeredProductId"];

    public static VerifiedHrReadEnvelope? Verify(string method, string rawTarget, string authorization,
        string idempotencyKey, string encodedEnvelope, string signatureHex, ReadOnlySpan<byte> trustedKey,
        HrReadReceiverSettings settings, DateTimeOffset now)
    {
        try
        {
            if (settings is null || !settings.Enabled || settings.Validate().Count != 0
                || !HrReadWire.TryParseTarget(method, rawTarget, out var target)
                || target is null || target.TenantId.ToString("D", CultureInfo.InvariantCulture) != settings.ConsumerTenantId
                || !ReadToken(authorization, out var token) || !ValidIdempotency(idempotencyKey)
                || encodedEnvelope is null || encodedEnvelope.Length is 0 or > HrReadWire.MaximumEnvelopeBytes
                || !LowerHex(signatureHex, 64)) return null;

            var raw = Convert.FromBase64String(encodedEnvelope);
            if (raw.Length is 0 or > MaximumDecodedEnvelopeBytes || Convert.ToBase64String(raw) != encodedEnvelope) return null;
            // Reject invalid UTF8 rather than signing parser replacement characters.
            _ = StrictUtf8.GetString(raw);
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            var envelope = document.RootElement;
            if (!OnlyKeys(envelope, EnvelopeKeys)
                || !EqualsText(envelope, "envelopeVersion", EnvelopeVersion)
                || !EqualsText(envelope, "schemaVersion", HrReadDesign.SchemaVersion)
                || !EqualsText(envelope, "profileId", HrReadDesign.ProfileId)
                || !EqualsText(envelope, "keyId", settings.KeyId) || !EqualsText(envelope, "tier", settings.Tier)
                || !Seconds(envelope.GetProperty("issuedAt"), out var issuedAt)
                || !Seconds(envelope.GetProperty("expiresAt"), out var expiresAt)
                || !Text(envelope, "tokenSha256", out var tokenHash) || !LowerHex(tokenHash, 64)
                || tokenHash != Hash(StrictUtf8.GetBytes(token))) return null;

            var frame = HrReadWire.BuildFrame(method, rawTarget, authorization, encodedEnvelope, idempotencyKey, []);
            if (!HrReadWire.Verify(frame, trustedKey, Convert.FromHexString(signatureHex))) return null;
            var instant = Epoch(now);
            if (instant < 0 || issuedAt > instant || expiresAt <= issuedAt || instant >= expiresAt
                || expiresAt - issuedAt > settings.MaximumWitnessSeconds) return null;

            var context = envelope.GetProperty("context");
            if (!OnlyKeys(context, ContextKeys)
                || !Text(context, "requestId", out var requestId) || requestId.Length != 34
                || !requestId.StartsWith("g-", StringComparison.Ordinal) || !LowerHex(requestId[2..], 32)
                || !EqualsText(context, "decisionId", requestId) || !EqualsText(context, "admissionId", "admission:" + requestId)
                || !EqualsText(context, "method", method) || !EqualsText(context, "rawTarget", rawTarget)
                || !EqualsText(context, "subjectId", settings.SubjectId) || !EqualsText(context, "clientId", settings.ClientId)
                || !EqualsText(context, "consumerTenantId", settings.ConsumerTenantId)
                || !EqualsNullableText(context.GetProperty("ownerTenantId"), settings.OwnerTenantId)
                || !EqualsText(context, "productId", settings.ProductKey) || !EqualsText(context, "audience", settings.Audience)
                || !EqualsText(context, "issuer", settings.Issuer) || !EqualsText(context, "scope", HrReadDesign.ReadScope)
                || !EqualsText(context, "environment", settings.Environment) || !EqualsText(context, "zone", settings.Zone)
                || !EqualsText(context, "action", HrReadDesign.Action(target.Kind))
                || !EqualsText(context, "operationGroup", HrReadDesign.Group)
                || !EqualsText(context, "contractId", settings.ContractId)
                || !IntegerEquals(context, "contractVersion", settings.ContractVersion)
                || !EqualsText(context, "policyId", settings.PolicyId) || !IntegerEquals(context, "policyVersion", settings.PolicyVersion)
                || !EqualsText(context, "allocationId", settings.AllocationId)
                || !EqualsText(context, "allocationScope", settings.CanonicalScope)
                || !IntegerEquals(context, "configRevision", settings.ConfigRevision)
                || context.GetProperty("allowed").ValueKind != JsonValueKind.True
                || context.GetProperty("confirmed").ValueKind != JsonValueKind.True) return null;

            var ownership = context.GetProperty("ownership");
            var expectedKind = settings.Owner == "platform" ? "Platform" : "Tenant";
            if (!OnlyKeys(ownership, OwnershipKeys) || !EqualsText(ownership, "kind", expectedKind)
                || !EqualsText(ownership, "owner", settings.Owner)
                || !EqualsText(ownership, "registeredProductId", settings.RegisteredProductId)
                || !Text(context, "allocationPeriodId", out var periodId)
                || !Integer(context.GetProperty("periodStartMs"), 0, MaximumSafeInteger, out var periodStartMs)
                || !Integer(context.GetProperty("periodEndMs"), 1, MaximumSafeInteger, out var periodEndMs)
                || !ValidPeriod(periodId, periodStartMs, periodEndMs)
                || !Integer(context.GetProperty("tokenExpiresAt"), 1, (long)MaximumEpochSeconds, out var tokenExpiresAt)
                || !Seconds(context.GetProperty("decisionValidUntil"), out var decisionValidUntil)
                || instant >= tokenExpiresAt || instant >= decisionValidUntil
                || decisionValidUntil - issuedAt > settings.MaximumWitnessSeconds
                || expiresAt > tokenExpiresAt || expiresAt > decisionValidUntil
                || issuedAt * 1000 < periodStartMs || instant * 1000 < periodStartMs || instant * 1000 >= periodEndMs
                || expiresAt * 1000 > periodEndMs) return null;

            return new(settings, target, new(expectedKind, settings.Owner, settings.RegisteredProductId),
                requestId, issuedAt, expiresAt, decisionValidUntil, tokenExpiresAt, periodId, periodStartMs, periodEndMs,
                tokenHash, method, rawTarget, authorization, idempotencyKey, encodedEnvelope);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException
            or InvalidOperationException or OverflowException or CryptographicException)
        { return null; }
    }

    private static bool ReadToken(string? authorization, out string token)
    {
        token = string.Empty;
        if (authorization is null || authorization.Length is < 8 or > HrReadWire.MaximumAuthorizationBytes
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        token = authorization[7..];
        return token.Length > 0 && token.All(c => c is >= '!' and <= '~' && c != ',');
    }

    private static bool ValidIdempotency(string? value) => value is not null && value.Length <= HrReadWire.MaximumIdempotencyBytes
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or ':' or '-');
    private static bool LowerHex(string? value, int length) => value?.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
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
            && number is >= 0 and <= MaximumEpochSeconds
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
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate envelope property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool ValidPeriod(string periodId, long starts, long ends)
    {
        if (!DateTimeOffset.TryParseExact(periodId + "-01T00:00:00Z", "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
            || start.Year < 1970 || start.Year == 9999 && start.Month == 12
            || periodId != start.ToString("yyyy-MM", CultureInfo.InvariantCulture)) return false;
        return start.ToUnixTimeMilliseconds() == starts && start.AddMonths(1).ToUnixTimeMilliseconds() == ends;
    }
    private static decimal Epoch(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (decimal)TimeSpan.TicksPerSecond;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
