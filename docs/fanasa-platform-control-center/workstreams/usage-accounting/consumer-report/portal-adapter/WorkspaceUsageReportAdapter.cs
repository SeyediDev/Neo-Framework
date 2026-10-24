using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Metering;

namespace Fanasa.UsageAccounting.PortalAdapter;

// The envelope must come from a centrally authorized reader. A caller-supplied subject
// is not delegation proof. This mapper adds no authentication, route or product registry.
public sealed record ConsumerUsageReportEnvelope(string Subject, ConsumerUsageReport Report);

public sealed record WorkspaceConsumerUsage(Guid OrganizationId, string Status,
    DateTimeOffset From, DateTimeOffset To, string PeriodLabel, string? AsOf,
    string? Currency, string? TotalAmount, IReadOnlyList<WorkspaceConsumerUsageItem> Items,
    string Basis = "posted-ledger");

public sealed record WorkspaceConsumerUsageItem(string ProductKey, string Name, string MeterId,
    string? MeterVersion, string? Unit, string? Quantity, string Amount, int ChargeCount);

/// <summary>Read-only presentation mapping, independent of central and Portal hosting.</summary>
public static class WorkspaceUsageReportAdapter
{
    private static readonly Regex PlainDecimal = new(@"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static WorkspaceConsumerUsage Map(string expectedSubject, Guid consumer,
        DateTimeOffset from, DateTimeOffset to, ConsumerUsageReportEnvelope? envelope,
        IReadOnlyDictionary<string, string>? productNames = null)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject) || consumer == Guid.Empty
            || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to
            || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("A subject, consumer and UTC period of at most 366 days are required.");

        var period = $"{UtcLabel(from)} تا پیش از {UtcLabel(to)} · UTC";
        WorkspaceConsumerUsage Empty(string status) => new(consumer, status, from, to, period, null, null, null, []);
        var report = envelope?.Report;
        if (report is null || !string.Equals(envelope!.Subject, expectedSubject, StringComparison.Ordinal)
            || report.ConsumerTenantId != consumer || report.From != from || report.To != to
            || report.From.Offset != TimeSpan.Zero || report.To.Offset != TimeSpan.Zero
            || report.Basis != "posted-ledger" || report.Items is null)
            return Empty("unavailable");

        if (report.Status is "notRecorded" or "incomplete" or "unavailable")
            return report.Currency is null && report.TotalAmount is null && report.Items.Count == 0
                ? Empty(report.Status) : Empty("unavailable");
        if (report.Status != "ready" || report.AsOf.Offset != TimeSpan.Zero || report.Items.Count > 10000)
            return Empty("unavailable");

        try
        {
            var minorScale = report.Currency switch
            {
                "IRR" => 0, "USD" or "EUR" => 2, _ => throw new InvalidDataException()
            };
            var expectedTotal = MoneyUnits(report.TotalAmount, minorScale);
            var items = new List<WorkspaceConsumerUsageItem>();
            var groups = new HashSet<(string Product, string Meter, string? Version, string? Unit)>();
            BigInteger total = 0;
            var count = 0;
            foreach (var item in report.Items)
            {
                if (item is null || !Label(item.ProductKey) || !Label(item.MeterId)
                    || item.ChargeCount is < 1 or > 10000
                    || !groups.Add((item.ProductKey, item.MeterId, item.MeterVersion, item.Unit)))
                    throw new InvalidDataException();
                var legacy = item.MeterVersion is null && item.Unit is null && item.Quantity is null;
                if (!legacy && (!Label(item.MeterVersion) || !Label(item.Unit) || ExactDecimal(item.Quantity) <= 0))
                    throw new InvalidDataException();
                total += MoneyUnits(item.Amount, minorScale);
                count = checked(count + item.ChargeCount);
                if (count > 10000) throw new InvalidDataException();
                var name = productNames is not null && productNames.TryGetValue(item.ProductKey, out var title) && Label(title)
                    ? title : item.ProductKey;
                items.Add(new(item.ProductKey, name, item.MeterId, item.MeterVersion,
                    item.Unit, item.Quantity, item.Amount, item.ChargeCount));
            }
            if (total != expectedTotal) throw new InvalidDataException();
            return new(consumer, "ready", from, to, period, report.AsOf.ToString("O", CultureInfo.InvariantCulture),
                report.Currency, report.TotalAmount, items);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or FormatException)
        {
            return Empty("unavailable");
        }
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
        var sourceCoefficient = BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture);
        var (coefficient, scale) = DecimalParts(value);
        if (sourceCoefficient * BigInteger.Pow(10, scale) != coefficient * BigInteger.Pow(10, sourceScale))
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
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        return (coefficient, (bits[3] >> 16) & 255);
    }
}
