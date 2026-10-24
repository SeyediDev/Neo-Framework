using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageAccounting.Sample;

// This parser implements the pilot's behavioral checks, not a general JSON Schema validator.
// The immutable resolver below pins synthetic contract/rate facts; it is not a live policy resolver.
public sealed record FixtureEvent(JsonElement Payload)
{
    public string EventId => Text("eventId");
    public Guid Owner => Guid.Parse(Text("ownerTenantId"));
    public Guid Consumer => Guid.Parse(Text("consumerTenantId"));
    public string Product => Text("productKey");
    public string Environment => Text("environment");
    public string OperationId => Text("operation", "id");
    public string Actor => Text("operation", "actorId");
    public string Meter => Text("meter", "id");
    public string Unit => Text("meter", "unit");
    public decimal Quantity => Decimal("meter", "quantity");
    public decimal UnitPrice => Decimal("rateSnapshot", "unitPrice");
    public string Currency => Text("rateSnapshot", "currency");
    public string BillingMode => Text("rateSnapshot", "billingMode");
    public int Scale => Element("rateSnapshot", "minorUnitScale").GetInt32();
    public DateTimeOffset AcceptedAt => Time("operation", "acceptedAt");
    public DateTimeOffset OccurredAt => Time("occurredAt");
    public DateTimeOffset ProducedAt => Time("producedAt");
    public DateTimeOffset RateStartsAt => Time("rateSnapshot", "effectiveFrom");
    public DateTimeOffset RateEndsAt => Time("rateSnapshot", "effectiveTo");
    public string BusinessReceipt => Text("evidence", "businessReceiptId");
    public string Reference => Text("evidence", "reference");
    public string EventFingerprint => Hash(Canonical(Payload));
    public string OperationFingerprint => Hash(Canonical(Payload, true));
    public string EventKey => "event:v1:" + Hash(JsonSerializer.Serialize(new[]
        { Text("producer", "id"), Environment, Consumer.ToString(), EventId }));
    // Business identity excludes event transport identity and never uses requestId as a charge key.
    public string BusinessKey => "measured:v1:" + Hash(JsonSerializer.Serialize(new[]
        { Environment, Consumer.ToString(), Product, Text("operation", "version"), OperationId,
          Text("operation", "billableBoundary"), Meter, Text("meter", "version") }));
    public decimal Amount => decimal.Round(checked(Quantity * UnitPrice), Scale, MidpointRounding.AwayFromZero);

    public static FixtureEvent Load(string path)
    { using var document = JsonDocument.Parse(File.ReadAllText(path)); var message = new FixtureEvent(document.RootElement.Clone()); _ = message.EventFingerprint; return message; }

    public FixtureEvent Change(Action<JsonObject> update)
    {
        var node = JsonNode.Parse(Payload.GetRawText())!.AsObject(); update(node);
        using var document = JsonDocument.Parse(node.ToJsonString()); return new(document.RootElement.Clone());
    }
    public FixtureEvent NewOperation(string id, string eventId) => Change(node =>
    {
        node["eventId"] = eventId; node["operation"]!["id"] = id;
        node["operation"]!["requestId"] = Guid.NewGuid().ToString();
        node["evidence"]!["businessReceiptId"] = "fixture-business-receipt-" + id;
        node["evidence"]!["reference"] = "fixture://organization/" + id;
    });
    public string Text(params string[] path) => Element(path).GetString() ?? throw new InvalidDataException(string.Join('.', path));
    public JsonElement Element(params string[] path)
    { var current = Payload; foreach (var segment in path) current = current.GetProperty(segment); return current; }
    public static string SnapshotFingerprint(JsonElement value) => Hash(Canonical(value));
    private decimal Decimal(params string[] path) => decimal.Parse(Text(path), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private DateTimeOffset Time(params string[] path)
    {
        var text = Text(path);
        if (!text.EndsWith('Z') || !DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            throw new InvalidDataException("UTC Z timestamp required: " + string.Join('.', path));
        return instant;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Canonical(JsonElement payload, bool operation = false)
    {
        using var buffer = new MemoryStream(); using var writer = new Utf8JsonWriter(buffer);
        Write(writer, payload, operation, ""); writer.Flush(); return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static void Write(Utf8JsonWriter writer, JsonElement element, bool operation, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON object name: " + property.Name);
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    var child = path.Length == 0 ? property.Name : path + "." + property.Name;
                    if (operation && child is "eventId" or "producedAt" or "correlationId" or "traceId" or "operation.requestId") continue;
                    writer.WritePropertyName(property.Name); Write(writer, property.Value, operation, child);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(writer, item, operation, path); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
}

public sealed class PinnedHistoricalResolver(params FixtureEvent[] admitted)
{
    public void Validate(FixtureEvent message)
    {
        var baseline = admitted.SingleOrDefault(x => x.Text("rateSnapshot", "rateId") == message.Text("rateSnapshot", "rateId")
            && x.Element("rateSnapshot", "rateVersion").GetInt32() == message.Element("rateSnapshot", "rateVersion").GetInt32())
            ?? throw new InvalidDataException("No trusted fixture historical rate/version exists.");
        if (message.Text("schemaVersion") != "1.0" || message.Text("outcome") != "completed"
            || message.Quantity != 1 || message.UnitPrice < 0 || message.BillingMode != "postpaid"
            || message.Scale != (message.Currency == "IRR" ? 0 : 2)
            || message.Text("rateSnapshot", "rounding") != "away-from-zero"
            || message.OccurredAt < message.AcceptedAt
            || message.ProducedAt < message.OccurredAt
            || message.AcceptedAt < message.RateStartsAt
            || message.AcceptedAt >= message.RateEndsAt)
            throw new InvalidDataException("The fixture only accepts a completed unit operation with its historical admitted rate.");
        // Trusted fixture values are pinned at startup. An event cannot authorize its own parties or rate.
        foreach (var name in new[] { "producer", "environment", "ownerTenantId", "consumerTenantId", "productKey", "contractId", "contractVersion", "allocationId", "allocationPeriodId", "authorization", "meter", "rateSnapshot" })
            if (FixtureEvent.SnapshotFingerprint(message.Element(name)) != FixtureEvent.SnapshotFingerprint(baseline.Element(name)))
                throw new InvalidDataException("Historical fixture binding mismatch: " + name);
        if (message.Text("operation", "version") != baseline.Text("operation", "version")
            || message.Text("operation", "billableBoundary") != baseline.Text("operation", "billableBoundary")
            || message.Actor != baseline.Actor)
            throw new InvalidDataException("The completed result is not the admitted pilot operation boundary.");
        _ = Guid.Parse(message.EventId); _ = message.BusinessReceipt; _ = message.Reference;
    }
}
