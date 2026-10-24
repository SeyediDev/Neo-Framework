using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fanasa.UnifiedPortal.Web;

// Deliberately excludes ledger identities, actors, business references and credentials.
public sealed record ConsumerAccountView(Guid OrganizationId, string Status, string? AsOf, string? Mode,
    string? Currency, string? WalletBalance, string? Outstanding, string? AvailableCredit,
    IReadOnlyList<ConsumerInvoiceView> Invoices);
public sealed record ConsumerInvoiceView(Guid Id, string Period, string Amount, string CreditedAmount,
    string PaidAmount, string RemainingAmount, string Status, string IssuedAt, string? From, string? To);

public static class ConsumerAccountProjection
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public static ConsumerAccountView Empty(Guid consumer, string status = "unavailable")
        => new(consumer, status, null, null, null, null, null, null, []);

    public static ConsumerAccountView Read(ReadOnlyMemory<byte> json, string expectedSubject, Guid consumer)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject) || consumer == Guid.Empty) throw new ArgumentException("A subject and consumer are required.");
        if (json.Length is 0 or > MaximumBytes) return Empty(consumer);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(document.RootElement);
            var root = document.RootElement;
            if (Text(root, "subject") != expectedSubject) return Empty(consumer);
            var account = root.GetProperty("account");
            if (account.GetProperty("schemaVersion").GetInt32() != 1 || account.GetProperty("consumerTenantId").GetGuid() != consumer)
                return Empty(consumer);
            var status = Text(account, "status");
            var asOf = Date(account, "asOf");
            var mode = Nullable(account, "mode"); var currency = Nullable(account, "currency");
            var wallet = Nullable(account, "walletBalance"); var outstanding = Nullable(account, "outstanding");
            var available = Nullable(account, "availableCredit"); var rows = account.GetProperty("invoices");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1000) return Empty(consumer);
            if (status is "notRecorded" or "unavailable")
                return mode is null && currency is null && wallet is null && outstanding is null && available is null && rows.GetArrayLength() == 0
                    ? Empty(consumer, status) : Empty(consumer);
            if (status != "ready" || mode is not ("payg" or "postpaid")) return Empty(consumer);
            var scale = currency switch { "IRR" => 0, "USD" or "EUR" => 2, _ => throw new InvalidDataException() };
            ConsumerUsageReportProjection.MoneyUnits(wallet, scale);
            ConsumerUsageReportProjection.MoneyUnits(outstanding, scale);
            ConsumerUsageReportProjection.MoneyUnits(available, scale);
            var ids = new HashSet<Guid>(); var periods = new HashSet<string>(StringComparer.Ordinal);
            var invoices = new List<ConsumerInvoiceView>();
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.GetProperty("id").GetGuid(); var period = Text(row, "period");
                if (id == Guid.Empty || !ids.Add(id) || !periods.Add(period)
                    || !DateTimeOffset.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var start)) throw new InvalidDataException();
                var amount = Text(row, "amount"); var credited = Text(row, "creditedAmount");
                var paid = Text(row, "paidAmount"); var remaining = Text(row, "remainingAmount");
                var amountUnits = ConsumerUsageReportProjection.MoneyUnits(amount, scale);
                var creditUnits = ConsumerUsageReportProjection.MoneyUnits(credited, scale);
                var paidUnits = ConsumerUsageReportProjection.MoneyUnits(paid, scale);
                var remainingUnits = ConsumerUsageReportProjection.MoneyUnits(remaining, scale);
                if (creditUnits + paidUnits + remainingUnits != amountUnits) throw new InvalidDataException();
                var invoiceStatus = Text(row, "status");
                var expectedStatus = mode == "payg" ? "statement" : remainingUnits.IsZero ? (creditUnits == amountUnits && creditUnits > 0 ? "credited" : "paid")
                    : paidUnits > 0 || creditUnits > 0 ? "partiallyPaid" : "issued";
                if (invoiceStatus != expectedStatus) throw new InvalidDataException();
                var issuedAt = Date(row, "issuedAt");
                var from = Nullable(row, "from"); var to = Nullable(row, "to");
                if ((from is null) != (to is null) || from is not null && (Utc(from) != start || Utc(to!) != start.AddMonths(1)))
                    throw new InvalidDataException();
                invoices.Add(new(id, period, amount, credited, paid, remaining, invoiceStatus,
                    issuedAt.ToString("O", CultureInfo.InvariantCulture), from, to));
            }
            return new(consumer, "ready", asOf.ToString("O", CultureInfo.InvariantCulture), mode, currency,
                wallet, outstanding, available, invoices.AsReadOnly());
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException
            or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentOutOfRangeException)
        { return Empty(consumer); }
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException();
    private static string? Nullable(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    private static DateTimeOffset Date(JsonElement value, string name) => Utc(Text(value, name));
    private static DateTimeOffset Utc(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$", RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date.Offset != TimeSpan.Zero || date == default) throw new InvalidDataException();
        return date;
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) { if (!names.Add(item.Name)) throw new InvalidDataException(); Unique(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
