using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record UsageProducer(string Id, string Kind, string DeploymentId);
public sealed record UsageOperation(string Id, string Version, string ActorId, Guid RequestId, string RequestFingerprint, DateTimeOffset AcceptedAt, string BillableBoundary);
public sealed record UsageAuthorization(string DecisionId, string PolicyVersion, string AdmissionId);
public sealed record UsageMeter(string Id, string Version, string Unit, string Quantity);
public sealed record UsageRateSnapshot(string RateId, int RateVersion, DateTimeOffset EffectiveFrom, DateTimeOffset EffectiveTo, string UnitPrice, string Currency, int MinorUnitScale, string Rounding, string BillingMode);
public sealed record UsageEvidence(string BusinessReceiptId, string ResultDigest, string Reference);

/// <summary>The limited organization unit.save UsageEvent 1.0 wire contract.</summary>
public sealed record UsageEventV1(string SchemaVersion, Guid EventId, DateTimeOffset OccurredAt, DateTimeOffset ProducedAt,
    UsageProducer Producer, string Environment, Guid OwnerTenantId, Guid ConsumerTenantId, string ProductKey,
    string ContractId, int ContractVersion, string AllocationId, string AllocationPeriodId, UsageOperation Operation,
    UsageAuthorization Authorization, UsageMeter Meter, UsageRateSnapshot RateSnapshot, string Outcome,
    UsageEvidence Evidence, Guid CorrelationId, string? TraceId = null)
{
    [JsonIgnore] internal OrganizationUsageAdmission? InvocationProof { get; init; }
    [JsonIgnore] public decimal Quantity => ParseDecimal(Meter.Quantity);
    [JsonIgnore] public decimal UnitPrice => ParseDecimal(RateSnapshot.UnitPrice);
    [JsonIgnore] public decimal Amount => decimal.Round(checked(Quantity * UnitPrice), RateSnapshot.MinorUnitScale, MidpointRounding.AwayFromZero);
    [JsonIgnore] public string EventKey => "event:v1:" + Hash(JsonSerializer.Serialize(new[] { Producer.Id, Environment, ConsumerTenantId.ToString("D"), EventId.ToString("D") }));
    [JsonIgnore] public string BusinessKey => "measured:v1:" + Hash(JsonSerializer.Serialize(new[]
        { Environment, ConsumerTenantId.ToString("D"), ProductKey, Operation.Version, Operation.Id, Operation.BillableBoundary, Meter.Id, Meter.Version }));
    [JsonIgnore] public string EventFingerprint => Fingerprint(false);
    [JsonIgnore] public string OperationFingerprint => Fingerprint(true);

    public string ToWireJson() { Validate(); return JsonSerializer.Serialize(this, WireOptions); }
    public static UsageEventV1 FromJson(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > 64 * 1024) throw new ArgumentException("UsageEvent exceeds its wire size limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateNames(document.RootElement);
            ValidateWireShape(document.RootElement);
            var message = JsonSerializer.Deserialize<UsageEventV1>(json, WireOptions) ?? throw new ArgumentException("UsageEvent is required.");
            message.Validate(); return message;
        }
        catch (JsonException exception) { throw new ArgumentException("Invalid UsageEvent wire contract.", exception); }
    }
    public void Validate()
    {
        if (SchemaVersion != "1.0" || EventId == Guid.Empty || OwnerTenantId == Guid.Empty || ConsumerTenantId == Guid.Empty || CorrelationId == Guid.Empty
            || Producer is null || Operation is null || Authorization is null || Meter is null || RateSnapshot is null || Evidence is null
            || Environment is not ("development" or "pilot" or "production") || Outcome != "completed" || ContractVersion < 1)
            throw new ArgumentException("UsageEvent identity, version or completed outcome is invalid.");
        Label(Producer.Id); Label(Producer.DeploymentId); Label(ProductKey); Label(ContractId); Label(AllocationId); Label(AllocationPeriodId);
        Label(Operation.Id); Label(Operation.Version); Label(Operation.ActorId); Label(Authorization.DecisionId); Label(Authorization.PolicyVersion); Label(Authorization.AdmissionId);
        Label(Evidence.BusinessReceiptId); Label(Evidence.Reference, 1000); Label(RateSnapshot.RateId);
        if (Producer.Kind != "product-backend" || Operation.RequestId == Guid.Empty
            || !Hex64.IsMatch(Operation.RequestFingerprint ?? "") || !Hex64.IsMatch(Evidence.ResultDigest ?? "")
            || TraceId is not null && !Hex32.IsMatch(TraceId)
            || Operation.BillableBoundary != "organization-unit-committed/v1"
            || Meter.Id != "organization.change" || Meter.Version != "1" || Meter.Unit != "change" || Meter.Quantity != "1")
            throw new ArgumentException("UsageEvent producer, operation, evidence or meter is invalid.");
        if (RateSnapshot.RateVersion < 1 || RateSnapshot.Currency is not ("IRR" or "USD" or "EUR")
            || RateSnapshot.MinorUnitScale != (RateSnapshot.Currency == "IRR" ? 0 : 2)
            || RateSnapshot.Rounding != "away-from-zero" || RateSnapshot.BillingMode != "postpaid")
            throw new ArgumentException("UsageEvent rate snapshot is invalid.");
        Utc(OccurredAt); Utc(ProducedAt); Utc(Operation.AcceptedAt); Utc(RateSnapshot.EffectiveFrom); Utc(RateSnapshot.EffectiveTo);
        if (Operation.AcceptedAt > OccurredAt || OccurredAt > ProducedAt || RateSnapshot.EffectiveFrom >= RateSnapshot.EffectiveTo
            || Operation.AcceptedAt < RateSnapshot.EffectiveFrom || Operation.AcceptedAt >= RateSnapshot.EffectiveTo)
            throw new ArgumentException("UsageEvent timeline or admitted rate window is invalid.");
        try { _ = Amount; }
        catch (OverflowException exception) { throw new ArgumentException("UsageEvent amount exceeds decimal precision.", exception); }
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static Guid StableGuid(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    internal static decimal ParseDecimal(string text)
    {
        if (text is null || !DecimalText.IsMatch(text) || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("UsageEvent decimal must be a bounded non-negative decimal string.");
        return value;
    }
    internal static void Label(string? value, int limit = 200)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > limit) throw new ArgumentException("A bounded nonempty UsageEvent label is required."); }
    private static void Utc(DateTimeOffset value)
    { if (value.Offset != TimeSpan.Zero) throw new ArgumentException("UsageEvent timestamps must be UTC."); }
    private string Fingerprint(bool operation)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(this, WireOptions));
        using var stream = new MemoryStream(); using var writer = new Utf8JsonWriter(stream);
        Canonical(writer, document.RootElement, operation, ""); writer.Flush(); return Hash(Encoding.UTF8.GetString(stream.ToArray()));
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement element, bool operation, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    var child = path.Length == 0 ? property.Name : path + "." + property.Name;
                    if (operation && child is "eventId" or "producedAt" or "correlationId" or "traceId" or "operation.requestId") continue;
                    writer.WritePropertyName(property.Name); Canonical(writer, property.Value, operation, child);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Canonical(writer, item, operation, path); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
    private static void RejectDuplicateNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate UsageEvent JSON name: " + property.Name); RejectDuplicateNames(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateNames(item);
    }
    private static void ValidateWireShape(JsonElement root)
    {
        ObjectShape(root, ["schemaVersion", "eventId", "occurredAt", "producedAt", "producer", "environment", "ownerTenantId", "consumerTenantId",
            "productKey", "contractId", "contractVersion", "allocationId", "allocationPeriodId", "operation", "authorization", "meter", "rateSnapshot",
            "outcome", "evidence", "correlationId"], "traceId");
        ObjectShape(root.GetProperty("producer"), ["id", "kind", "deploymentId"]);
        ObjectShape(root.GetProperty("operation"), ["id", "version", "actorId", "requestId", "requestFingerprint", "acceptedAt", "billableBoundary"]);
        ObjectShape(root.GetProperty("authorization"), ["decisionId", "policyVersion", "admissionId"]);
        ObjectShape(root.GetProperty("meter"), ["id", "version", "unit", "quantity"]);
        ObjectShape(root.GetProperty("rateSnapshot"), ["rateId", "rateVersion", "effectiveFrom", "effectiveTo", "unitPrice", "currency", "minorUnitScale", "rounding", "billingMode"]);
        ObjectShape(root.GetProperty("evidence"), ["businessReceiptId", "resultDigest", "reference"]);
    }
    private static void ObjectShape(JsonElement element, string[] required, params string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("UsageEvent object required.");
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
                throw new ArgumentException("Unknown or null UsageEvent property: " + property.Name);
        foreach (var name in required) if (!element.TryGetProperty(name, out _)) throw new ArgumentException("Required UsageEvent property missing: " + name);
    }
    private static readonly Regex DecimalText = new("\\A(0|[1-9][0-9]{0,17})(\\.[0-9]{1,6})?\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex32 = new("\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();
    private static JsonSerializerOptions CreateWireOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 16
        };
        options.Converters.Add(new UtcWireConverter()); return options;
    }
    private sealed class UtcWireConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("UTC Z timestamp string required.");
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') || !DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                throw new JsonException("UTC Z timestamp required.");
            return instant;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            Utc(value); writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        }
    }
}

public interface IUsageSnapshotValidator
{
    /// <summary>Validate trusted bindings only for a genuinely new operation, after durable replay checks.</summary>
    void ValidateNew(UsageEventV1 message);
}
