using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fanasa.UnifiedPortal.Web;

// Presentation contract only. No reference to the central Web assembly, credential,
// grant, tariff, wallet or ledger write belongs in this projection.
public sealed record ConsumerUsageView(Guid OrganizationId, string Status,
    DateTimeOffset From, DateTimeOffset To, string PeriodLabel, string? AsOf,
    string? Currency, string? TotalAmount, IReadOnlyList<ConsumerUsageViewItem> Items)
{
    public string Basis => "posted-ledger";
    public int? ServiceCount => Status == "ready"
        ? Items.Select(x => x.ProductKey).Distinct(StringComparer.Ordinal).Count() : null;
}

public sealed record ConsumerUsageViewItem(string ProductKey, string Name, string MeterId,
    string? MeterVersion, string? Unit, string? Quantity, string Amount, int ChargeCount);

/// <summary>
/// Validates the v1 central usage-report JSON before projecting it to a browser.
/// The hosting reader must authenticate the human credential and authorize the live
/// consumer. Matching the envelope is not proof of delegation or billing.read.
/// </summary>
public static class ConsumerUsageReportProjection
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly Regex PlainDecimal = new(@"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static ConsumerUsageView Read(ReadOnlyMemory<byte> json, string expectedSubject,
        Guid consumer, DateTimeOffset from, DateTimeOffset to,
        IReadOnlyDictionary<string, string>? approvedProductNames = null)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject) || consumer == Guid.Empty
            || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to
            || to - from >= TimeSpan.FromDays(366))
            throw new ArgumentException("A subject, consumer and UTC period shorter than 366 days are required.");

        var period = $"{UtcLabel(from)} تا پیش از {UtcLabel(to)} · UTC";
        ConsumerUsageView Empty(string status) => new(consumer, status, from, to, period, null, null, null, []);
        if (json.Length is 0 or > MaximumBytes) return Empty("unavailable");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueProperties(document.RootElement);
            var root = document.RootElement;
            if (Text(root, "subject") != expectedSubject) return Empty("unavailable");
            var report = root.GetProperty("report");
            if (report.GetProperty("consumerTenantId").GetGuid() != consumer
                || Utc(report, "from") != from || Utc(report, "to") != to
                || Text(report, "basis") != "posted-ledger") return Empty("unavailable");
            var asOf = Utc(report, "asOf");
            var status = Text(report, "status");
            var currency = NullableText(report, "currency");
            var amount = NullableText(report, "totalAmount");
            var rows = report.GetProperty("items");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000)
                return Empty("unavailable");
            if (status is "notRecorded" or "incomplete" or "unavailable")
                return currency is null && amount is null && rows.GetArrayLength() == 0
                    ? Empty(status) : Empty("unavailable");
            if (status != "ready") return Empty("unavailable");
            var scale = currency switch { "IRR" => 0, "USD" or "EUR" => 2, _ => throw new InvalidDataException() };
            var expectedTotal = MoneyUnits(amount, scale);
            var groups = new HashSet<(string, string, string?, string?)>();
            var items = new List<ConsumerUsageViewItem>();
            BigInteger total = 0;
            var chargeCount = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var product = Text(row, "productKey");
                var meter = Text(row, "meterId");
                var version = NullableText(row, "meterVersion");
                var unit = NullableText(row, "unit");
                var quantity = NullableText(row, "quantity");
                var rowAmount = Text(row, "amount");
                var count = row.GetProperty("chargeCount").GetInt32();
                if (!Label(product) || !Label(meter) || count is < 1 or > 10000
                    || !groups.Add((product, meter, version, unit))) throw new InvalidDataException();
                var legacy = version is null && unit is null && quantity is null;
                if (!legacy && (!Label(version) || !Label(unit) || ExactDecimal(quantity) <= 0))
                    throw new InvalidDataException();
                total += MoneyUnits(rowAmount, scale);
                chargeCount = checked(chargeCount + count);
                if (chargeCount > 10000) throw new InvalidDataException();
                var name = approvedProductNames is not null && approvedProductNames.TryGetValue(product, out var title)
                    && Label(title) ? title : product;
                items.Add(new(product, name, meter, version, unit, quantity, rowAmount, count));
            }
            if (total != expectedTotal) return Empty("unavailable");
            return new(consumer, "ready", from, to, period, asOf.ToString("O", CultureInfo.InvariantCulture),
                currency, amount, items.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
            or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            // Failed/foreign responses never retain financial values from a previous request.
            return Empty("unavailable");
        }
    }

    private static string Text(JsonElement value, string name)
        => value.GetProperty(name).GetString() ?? throw new InvalidDataException();
    private static string? NullableText(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }
    private static DateTimeOffset Utc(JsonElement value, string name)
    {
        var text = Text(value, name);
        if (!text.EndsWith('Z') && !text.EndsWith("+00:00", StringComparison.Ordinal))
            throw new InvalidDataException();
        var result = value.GetProperty(name).GetDateTimeOffset();
        if (result.Offset != TimeSpan.Zero || result == default) throw new InvalidDataException();
        return result;
    }
    private static string UtcLabel(DateTimeOffset value) => value.ToString(
        value.TimeOfDay == TimeSpan.Zero ? "yyyy/MM/dd" : "yyyy/MM/dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    private static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200
        && !value.Any(char.IsControl);
    private static decimal ExactDecimal(string? text)
    {
        if (text is null || text.Length > 64 || !PlainDecimal.IsMatch(text)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException();
        var dot = text.IndexOf('.');
        var sourceScale = dot < 0 ? 0 : text.Length - dot - 1;
        var source = BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture);
        var (coefficient, scale) = DecimalParts(value);
        if (source * BigInteger.Pow(10, scale) != coefficient * BigInteger.Pow(10, sourceScale))
            throw new InvalidDataException();
        return value;
    }
    private static BigInteger MoneyUnits(string? text, int minorScale)
    {
        var (coefficient, scale) = DecimalParts(ExactDecimal(text));
        while (scale > minorScale && coefficient % 10 == 0) { coefficient /= 10; scale--; }
        if (scale > minorScale) throw new InvalidDataException();
        return coefficient * BigInteger.Pow(10, minorScale - scale);
    }
    private static (BigInteger Coefficient, int Scale) DecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        return ((BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64),
            (bits[3] >> 16) & 255);
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
}
