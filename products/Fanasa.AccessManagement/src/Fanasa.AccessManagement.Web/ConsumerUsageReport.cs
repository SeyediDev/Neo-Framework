using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record ConsumerUsageItem(string ProductKey, string MeterId, string? MeterVersion,
    string? Unit, string? Quantity, string Amount, int ChargeCount);
public sealed record ConsumerUsageReport(Guid ConsumerTenantId, DateTimeOffset From, DateTimeOffset To,
    DateTimeOffset AsOf, string Status, string? Currency, string? TotalAmount,
    IReadOnlyList<ConsumerUsageItem> Items, string? ReasonCode = null, string Basis = "posted-ledger");

/// <summary>
/// Read-only period projection over the existing Fabric ledger. The hosting boundary must
/// authorize the live consumer's billing.read before calling this store. No route or credential is added.
/// </summary>
public sealed class ConsumerUsageReportStore(FabricDatabase database, TimeProvider? timeProvider = null)
{
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public ConsumerUsageReport Read(Guid consumer, DateTimeOffset from, DateTimeOffset to, int maxRows = 10000)
    {
        if (consumer == Guid.Empty || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero
            || from >= to || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("A consumer and a UTC half-open period of at most 366 days are required.");
        if (maxRows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxRows));
        return database.ReadSnapshot(() => ReadCore(consumer, from, to, maxRows));
    }

    /// <summary>
    /// Authorizes and reads inside one query-only Fabric snapshot.
    /// The callback is checked before reading and immediately before returning.
    /// </summary>
    public ConsumerUsageReport Read(Guid consumer, DateTimeOffset from, DateTimeOffset to,
        Func<bool> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if (consumer == Guid.Empty || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero
            || from >= to || to - from >= TimeSpan.FromDays(366))
            throw new ArgumentException(
                "A consumer and a UTC half-open period shorter than 366 days is required.");

        return database.ReadSnapshot(() =>
        {
            if (!authorize()) throw new UnauthorizedAccessException();
            var report = ReadCore(consumer, from, to, 10000);
            if (!authorize()) throw new UnauthorizedAccessException();
            return report;
        });
    }

    private ConsumerUsageReport ReadCore(Guid consumer, DateTimeOffset from, DateTimeOffset to, int maxRows)
    {
        var asOf = clock.GetUtcNow().ToUniversalTime();
        ConsumerUsageReport Empty(string status, string code)
            => new(consumer, from, to, asOf, status, null, null, [], code);
        var document = database.ReadDocument("accounting", consumer.ToString());
        if (document is null) return Empty("notRecorded", "account-not-recorded");
        if (Encoding.UTF8.GetByteCount(document.Json) > MaximumDocumentBytes)
            return Empty("incomplete", "document-limit-exceeded");
        try
        {
            using var json = StrictDocument(document.Json);
            var root = json.RootElement;
            RequireProperties(root, "TenantId", "Revision", "Mode", "Currency", "Entries");
            if (!root.GetProperty("TenantId").TryGetGuid(out var tenant) || tenant != consumer)
                return Empty("unavailable", "account-consumer-mismatch");
            var rows = root.GetProperty("Entries");
            if (rows.ValueKind != JsonValueKind.Array) return Empty("unavailable", "invalid-ledger");
            if (rows.GetArrayLength() > maxRows) return Empty("incomplete", "scan-limit-exceeded");
            foreach (var row in rows.EnumerateArray())
            {
                RequireProperties(row, "Quantity", "Amount");
                RequireExactDecimal(row.GetProperty("Quantity"));
                RequireExactDecimal(row.GetProperty("Amount"));
                if (row.TryGetProperty("UnitPrice", out var price) && price.ValueKind != JsonValueKind.Null)
                    RequireExactDecimal(price);
            }
            var account = JsonSerializer.Deserialize<BillingAccount>(document.Json)
                ?? throw new InvalidDataException();
            if (account.TenantId != consumer || account.Revision < 0 || account.Mode is not ("payg" or "postpaid")
                || account.Currency is not ("IRR" or "USD" or "EUR") || account.Entries is null)
                throw new InvalidDataException();

            var ids = new HashSet<Guid>();
            var keys = new HashSet<(string Kind, string Key)>();
            var aggregates = new Dictionary<Group, Aggregate>();
            decimal total = 0;
            var index = 0;
            foreach (var entry in account.Entries)
            {
                RequireProperties(rows[index++], "Id", "Key", "Kind", "Quantity", "Amount", "At");
                if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id)
                    || !Label(entry.Key) || !Label(entry.Kind) || !keys.Add((entry.Kind, entry.Key))
                    || entry.At.Offset != TimeSpan.Zero || entry.Kind is not ("usage" or "credit" or "payment" or "receipt"))
                    throw new InvalidDataException();
                if (entry.Kind != "usage") continue;
                RequireProperties(rows[index - 1], "ProductKey", "Metric");
                if (!Label(entry.ProductKey) || !Label(entry.Metric) || entry.Quantity <= 0 || entry.Amount < 0
                    || entry.UnitPrice < 0 || entry.UsageOccurredAt.HasValue && entry.UsageOccurredAt.Value.Offset != TimeSpan.Zero
                    || decimal.Round(entry.Amount, account.Currency == "IRR" ? 0 : 2) != entry.Amount)
                    throw new InvalidDataException();
                if (entry.Key.StartsWith("organization:", StringComparison.Ordinal))
                {
                    var parts = entry.Key.Split(':');
                    if (parts.Length != 3 || !Guid.TryParse(parts[1], out var owner) || owner != consumer
                        || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
                        throw new InvalidDataException();
                }

                string? version = null, unit = null;
                var measured = entry.Key.StartsWith("measured:", StringComparison.Ordinal);
                if (measured)
                {
                    var operationDocument = database.ReadDocument("measured.operation", entry.Key)
                        ?? throw new InvalidDataException();
                    if (Encoding.UTF8.GetByteCount(operationDocument.Json) > 128 * 1024)
                        throw new InvalidDataException();
                    using var operationJson = StrictDocument(operationDocument.Json);
                    RequireProperties(operationJson.RootElement, "BusinessKey", "Fingerprint", "EventJson", "Status",
                        "Amount", "ReceivedAt", "CommittedAt");
                    RequireExactDecimal(operationJson.RootElement.GetProperty("Amount"));
                    var operation = JsonSerializer.Deserialize<MeasuredUsageOperation>(operationDocument.Json)
                        ?? throw new InvalidDataException();
                    var usage = UsageEventV1.FromJson(operation.EventJson);
                    RequireExactDecimalText(usage.Meter.Quantity);
                    RequireExactDecimalText(usage.RateSnapshot.UnitPrice);
                    if (operation.Status != "committed" || operation.Error is not null || operation.BusinessKey != entry.Key
                        || usage.BusinessKey != entry.Key || operation.Fingerprint != usage.OperationFingerprint
                        || usage.ConsumerTenantId != consumer || usage.ProductKey != entry.ProductKey
                        || usage.Meter.Id != entry.Metric || usage.Quantity != entry.Quantity
                        || usage.Amount != entry.Amount || operation.Amount != entry.Amount
                        || usage.RateSnapshot.Currency != account.Currency || usage.RateSnapshot.BillingMode != account.Mode
                        || usage.UnitPrice != entry.UnitPrice || usage.OccurredAt != entry.UsageOccurredAt
                        || usage.Operation.ActorId != entry.Actor || usage.Evidence.Reference != entry.Reference
                        || entry.Id != UsageEventV1.StableGuid(entry.Key + ":charge") || entry.InvoiceId is not null
                        || operation.ReceivedAt.Offset != TimeSpan.Zero || !operation.CommittedAt.HasValue
                        || operation.CommittedAt.Value.Offset != TimeSpan.Zero || operation.ReceivedAt > operation.CommittedAt)
                        throw new InvalidDataException();
                    version = usage.Meter.Version; unit = usage.Meter.Unit;
                }
                var occurred = entry.UsageOccurredAt ?? entry.At;
                if (occurred < from || occurred >= to) continue;
                // Legacy rows do not retain meter version/unit. Never invent them from current tariffs.
                var group = new Group(entry.ProductKey!, entry.Metric!, version, unit);
                aggregates.TryGetValue(group, out var previous);
                aggregates[group] = new(AddExact(previous.Amount, entry.Amount),
                    measured ? AddExact(previous.Quantity, entry.Quantity) : 0, checked(previous.Count + 1));
                total = AddExact(total, entry.Amount);
            }
            var items = aggregates.OrderBy(x => x.Key.Product, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Meter, StringComparer.Ordinal).ThenBy(x => x.Key.Version, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Unit, StringComparer.Ordinal)
                .Select(x => new ConsumerUsageItem(x.Key.Product, x.Key.Meter, x.Key.Version, x.Key.Unit,
                    x.Key.Unit is null ? null : Exact(x.Value.Quantity), Exact(x.Value.Amount), x.Value.Count)).ToArray();
            return new(consumer, from, to, asOf, "ready", account.Currency, Exact(total), items);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException
            or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            return Empty("unavailable", "invalid-ledger-or-binding");
        }
    }

    private static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static string Exact(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal AddExact(decimal left, decimal right)
    {
        var result = checked(left + right);
        var (leftCoefficient, leftScale) = DecimalParts(left);
        var (rightCoefficient, rightScale) = DecimalParts(right);
        var (resultCoefficient, resultScale) = DecimalParts(result);
        var scale = Math.Max(leftScale, rightScale);
        var exactCoefficient = leftCoefficient * BigInteger.Pow(10, scale - leftScale)
            + rightCoefficient * BigInteger.Pow(10, scale - rightScale);
        if (exactCoefficient * BigInteger.Pow(10, resultScale) != resultCoefficient * BigInteger.Pow(10, scale))
            throw new InvalidDataException();
        return result;
    }
    private static (BigInteger Coefficient, int Scale) DecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
        return (coefficient, (bits[3] >> 16) & 255);
    }
    private static void RequireExactDecimal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) throw new InvalidDataException();
        RequireExactDecimalText(value.GetRawText());
    }
    private static void RequireExactDecimalText(string raw)
    {
        // Fabric's decimal serializer emits plain numeric tokens, without exponents.
        if (raw.Length > 64 || !decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var parsed)) throw new InvalidDataException();
        var dot = raw.IndexOf('.');
        var sourceScale = dot < 0 ? 0 : raw.Length - dot - 1;
        var source = BigInteger.Parse(raw.Replace(".", ""), CultureInfo.InvariantCulture);
        var (coefficient, scale) = DecimalParts(parsed);
        if (source * BigInteger.Pow(10, scale) != coefficient * BigInteger.Pow(10, sourceScale))
            throw new InvalidDataException();
    }
    private static JsonDocument StrictDocument(string value)
    {
        var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
        try { UniqueProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || names.Any(name => !value.TryGetProperty(name, out _)))
            throw new InvalidDataException();
    }
    private sealed record Group(string Product, string Meter, string? Version, string? Unit);
    private readonly record struct Aggregate(decimal Amount, decimal Quantity, int Count);
}
