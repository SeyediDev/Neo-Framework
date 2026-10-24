using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Payments;

namespace Fanasa.AccessManagement.Web.Accounting;

public sealed record BillingInvoiceCommand(long ExpectedRevision, string Key, string Period, string Reference);
public sealed record BillingCreditNoteCommand(long ExpectedRevision, string Key, Guid ChargeId, Guid? InvoiceId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ExactBillingDecimalConverter))] decimal Amount, string Reason, string Reference);
public sealed record BillingInvoiceLine(Guid EntryId, string LedgerKey, string ProductKey, string Metric,
    decimal Quantity, decimal? UnitPrice, decimal GrossAmount, decimal CreditAmount, decimal NetAmount,
    DateTimeOffset OccurredAt, DateTimeOffset RecordedAt);

/// <summary>Exact local ledger arithmetic. Credit notes restore local account credit; they do not refund or transfer money.</summary>
public static class BillingBalance
{
    private const int MaximumAccountDocumentBytes = 8 * 1024 * 1024;
    public static decimal Sum(IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        decimal total = 0;
        foreach (var value in values) total = Add(total, value);
        return total;
    }
    public static decimal Add(decimal left, decimal right)
    {
        var result = checked(left + right);
        var (a, aScale) = Parts(left); var (b, bScale) = Parts(right); var (c, cScale) = Parts(result);
        var scale = Math.Max(aScale, bScale);
        var exact = a * BigInteger.Pow(10, scale - aScale) + b * BigInteger.Pow(10, scale - bScale);
        if (exact * BigInteger.Pow(10, cScale) != c * BigInteger.Pow(10, scale))
            throw new OverflowException("The financial result cannot be represented exactly.");
        return result;
    }
    public static decimal Subtract(decimal left, decimal right) => Add(left, -right);
    public static decimal Charge(decimal quantity, decimal price, string currency)
    {
        if (quantity <= 0 || price < 0) throw new ArgumentException("Positive quantity and nonnegative price are required.");
        var (q, qs) = Parts(quantity); var (p, ps) = Parts(price); var scale = MinorScale(currency);
        var coefficient = q * p; var sourceScale = qs + ps;
        if (sourceScale > scale)
        {
            var divisor = BigInteger.Pow(10, sourceScale - scale);
            var rounded = BigInteger.DivRem(coefficient, divisor, out var remainder);
            coefficient = rounded + (remainder * 2 >= divisor ? BigInteger.One : BigInteger.Zero);
        }
        else coefficient *= BigInteger.Pow(10, scale - sourceScale);
        var text = coefficient.ToString(CultureInfo.InvariantCulture);
        if (scale > 0) { text = text.PadLeft(scale + 1, '0'); text = text.Insert(text.Length - scale, "."); }
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result))
            throw new OverflowException("The charge exceeds decimal capacity.");
        var (actual, actualScale) = Parts(result);
        if (coefficient * BigInteger.Pow(10, actualScale) != actual * BigInteger.Pow(10, scale))
            throw new OverflowException("The charge cannot retain its exact minor units.");
        return result;
    }
    public static int MinorScale(string currency) => currency switch
    { "IRR" => 0, "USD" or "EUR" => 2, _ => throw new InvalidDataException("Unknown account currency.") };
    public static bool ValidMoney(decimal value, string currency) => value >= 0 && decimal.Round(value, MinorScale(currency)) == value;
    public static decimal InvoicePaid(BillingAccount account, Guid invoiceId)
        => Sum(account.Entries.Where(x => x.Kind == "payment" && x.InvoiceId == invoiceId).Select(x => x.Amount));
    public static decimal InvoiceCredits(BillingAccount account, Guid invoiceId)
        => Sum(account.Entries.Where(x => x.Kind == "credit-note" && x.InvoiceId == invoiceId).Select(x => x.Amount));
    public static decimal InvoiceRemaining(BillingAccount account, Guid invoiceId)
    {
        var invoice = account.Invoices.SingleOrDefault(x => x.Id == invoiceId) ?? throw new KeyNotFoundException();
        var remaining = Subtract(Subtract(invoice.Amount, InvoiceCredits(account, invoiceId)), InvoicePaid(account, invoiceId));
        if (remaining < 0) throw new InvalidDataException("Invoice applications exceed its immutable amount.");
        return remaining;
    }
    public static string Period(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static (DateTimeOffset From, DateTimeOffset To) PeriodBounds(string? period)
    {
        if (period is not { Length: 7 } || !DateTime.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var start) || start.Year == 9999 && start.Month == 12)
            throw new ArgumentException("A canonical Gregorian monthly period is required.");
        var from = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
        return (from, from.AddMonths(1));
    }
    internal static BillingAccount Deserialize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumAccountDocumentBytes) throw new InvalidDataException("Accounting document exceeds its bound.");
        using var document = StrictJson(json);
        Require(document.RootElement, "TenantId", "Revision", "Mode", "Currency", "CreditLimit", "Tariffs", "Entries", "Invoices");
        var entries = document.RootElement.GetProperty("Entries");
        if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Ledger entries are required.");
        foreach (var entry in entries.EnumerateArray()) Require(entry, "Id", "Key", "Kind", "Quantity", "Amount", "At", "Actor", "Reference");
        return document.RootElement.Deserialize<BillingAccount>() ?? throw new InvalidDataException();
    }
    internal static void EnsureWriteCapacity(BillingAccount account)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(account).Length > MaximumAccountDocumentBytes)
            throw new InvalidOperationException("The resulting accounting document exceeds its supported read capacity.");
    }
    internal static void Require(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || names.Any(name => !root.TryGetProperty(name, out _)))
            throw new InvalidDataException("Missing persisted financial field.");
    }
    internal static JsonDocument StrictJson(string json)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        try { ValidateJson(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate accounting JSON field."); ValidateJson(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var row in element.EnumerateArray()) ValidateJson(row);
        else if (element.ValueKind == JsonValueKind.Number)
        {
            var raw = element.GetRawText();
            if (raw.Length > 64 || !decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)) throw new InvalidDataException("Invalid exact financial number.");
            var dot = raw.IndexOf('.'); var scale = dot < 0 ? 0 : raw.Length - dot - 1;
            var source = BigInteger.Parse(raw.Replace(".", ""), CultureInfo.InvariantCulture); var (coefficient, actualScale) = Parts(parsed);
            if (source * BigInteger.Pow(10, actualScale) != coefficient * BigInteger.Pow(10, scale))
                throw new InvalidDataException("Financial numeric precision was lost.");
        }
    }
    internal static void Validate(BillingAccount account, Guid tenant)
    {
        if (account.TenantId != tenant || account.Revision < 0 || account.Mode is not ("payg" or "postpaid")
            || !ValidMoney(account.CreditLimit, account.Currency) || account.Entries is null || account.Invoices is null || account.Tariffs is null)
            throw new InvalidDataException("Invalid account identity or configuration.");
        var ids = new HashSet<Guid>(); var keys = new HashSet<(string, string)>();
        foreach (var entry in account.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) || !Label(entry.Key) || !Label(entry.Actor)
                || !Label(entry.Reference, entry.Kind == "receipt" ? 32768 : 1000) || !keys.Add((entry.Kind, entry.Key)) || entry.At.Offset != TimeSpan.Zero
                || entry.Kind is not ("usage" or "credit" or "payment" or "receipt" or "credit-note") || !ValidMoney(entry.Amount, account.Currency))
                throw new InvalidDataException("Invalid ledger row.");
            if (entry.Kind == "usage" && (!Label(entry.ProductKey) || !Label(entry.Metric) || entry.Quantity <= 0 || entry.UnitPrice < 0
                || entry.InvoiceId is not null || entry.RelatedEntryId is not null || entry.UsageOccurredAt is { Offset: var offset } && offset != TimeSpan.Zero))
                throw new InvalidDataException("Invalid posted consumption.");
            if (entry.Kind == "credit-note" && (entry.Amount <= 0 || entry.RelatedEntryId is null || !Label(entry.Reason, 1000) || entry.Quantity != 0))
                throw new InvalidDataException("Invalid credit note.");
        }
        var periods = new HashSet<string>(StringComparer.Ordinal); var invoiceIds = new HashSet<Guid>();
        foreach (var invoice in account.Invoices)
        {
            if (invoice is null || invoice.Id == Guid.Empty || !invoiceIds.Add(invoice.Id) || !periods.Add(invoice.Period)
                || invoice.IssuedAt.Offset != TimeSpan.Zero || !ValidMoney(invoice.Amount, account.Currency))
                throw new InvalidDataException("Invalid invoice identity or amount.");
            var (from, to) = PeriodBounds(invoice.Period);
            if (invoice.IssuedAt < to) throw new InvalidDataException("Invoice was issued before its period ended.");
            if (invoice.Lines is null)
            {
                if (invoice.ContentHash is not null || invoice.Currency is not null || invoice.From is not null || invoice.To is not null)
                    throw new InvalidDataException("Incomplete invoice snapshot.");
                continue; // Historical four-field invoices remain readable and are never rewritten.
            }
            if (invoice.Currency != account.Currency || invoice.From != from || invoice.To != to || invoice.From.Value.Offset != TimeSpan.Zero
                || invoice.To.Value.Offset != TimeSpan.Zero || invoice.SourceRevision is null || invoice.SourceRevision < 0
                || invoice.SourceRevision >= account.Revision || !Label(invoice.Actor) || !Label(invoice.Reference, 1000)
                || invoice.Basis != "posted-ledger" || invoice.ContentHash != InvoiceHash(invoice))
                throw new InvalidDataException("Invoice snapshot is inconsistent.");
            var lineIds = new HashSet<Guid>();
            foreach (var line in invoice.Lines)
            {
                if (line is null) throw new InvalidDataException("Null invoice line.");
                var charge = account.Entries.SingleOrDefault(x => x.Id == line.EntryId && x.Kind == "usage");
                if (charge is null || !lineIds.Add(line.EntryId) || line.LedgerKey != charge.Key || line.ProductKey != charge.ProductKey
                    || line.Metric != charge.Metric || line.Quantity != charge.Quantity || line.UnitPrice != charge.UnitPrice
                    || line.GrossAmount != charge.Amount || line.OccurredAt != (charge.UsageOccurredAt ?? charge.At) || line.RecordedAt != charge.At
                    || line.OccurredAt < from || line.OccurredAt >= to || !ValidMoney(line.CreditAmount, account.Currency)
                    || line.CreditAmount > line.GrossAmount || line.NetAmount != Subtract(line.GrossAmount, line.CreditAmount))
                    throw new InvalidDataException("Invoice line differs from its posted charge.");
                var beforeCredits = Sum(account.Entries.Where(x => x.Kind == "credit-note" && x.RelatedEntryId == charge.Id && x.InvoiceId is null).Select(x => x.Amount));
                if (line.CreditAmount != beforeCredits) throw new InvalidDataException("Invoice prior credits differ from its frozen line.");
            }
            if (Sum(invoice.Lines.Select(x => x.NetAmount)) != invoice.Amount
                || account.Entries.Count(x => x.Kind == "usage" && (x.UsageOccurredAt ?? x.At) >= from && (x.UsageOccurredAt ?? x.At) < to) != invoice.Lines.Length)
                throw new InvalidDataException("Invoice coverage or amount differs from its snapshot.");
        }
        foreach (var noteGroup in account.Entries.Where(x => x.Kind == "credit-note").GroupBy(x => x.RelatedEntryId))
        {
            var charge = account.Entries.SingleOrDefault(x => x.Id == noteGroup.Key && x.Kind == "usage") ?? throw new InvalidDataException("Credit target is missing.");
            if (Sum(noteGroup.Select(x => x.Amount)) > charge.Amount) throw new InvalidDataException("Credits exceed the charge.");
            foreach (var note in noteGroup)
                if (note.InvoiceId.HasValue && !account.Invoices.Any(x => x.Id == note.InvoiceId && x.Period == Period(charge.UsageOccurredAt ?? charge.At)))
                    throw new InvalidDataException("Credit target invoice differs from its charge.");
        }
        foreach (var payment in account.Entries.Where(x => x.Kind == "payment"))
            if (account.Mode != "postpaid" || payment.Amount <= 0 || !account.Invoices.Any(x => x.Id == payment.InvoiceId))
                throw new InvalidDataException("Payment invoice is missing.");
        foreach (var invoice in account.Invoices) _ = InvoiceRemaining(account, invoice.Id);
    }
    internal static string InvoiceHash(Invoice invoice) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(invoice with { ContentHash = null }))).ToLowerInvariant();
    internal static bool Label([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, int max = 200)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
        return (coefficient, (bits[3] >> 16) & 255);
    }
}

public sealed partial class AccountingStore
{
    public AccountView IssueInvoice(Guid tenant, BillingInvoiceCommand command, string actor)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Key?.StartsWith("measured:", StringComparison.Ordinal) == true
            || command.Key?.StartsWith("organization:", StringComparison.Ordinal) == true
            || command.Key?.StartsWith("zarinpal:", StringComparison.Ordinal) == true
            || command.Key?.StartsWith("billing-note:", StringComparison.Ordinal) == true)
            throw new ArgumentException("Invoice request keys cannot occupy internal financial or business identities.");
        return Execute(tenant, new(command.ExpectedRevision, "invoice", command.Key!, null, null, 0, null, null, 0, 0, 0,
            command.Reference, command.Period, null), actor);
    }

    public AccountView ApplyCreditNote(Guid tenant, BillingCreditNoteCommand command, string actor)
    {
        if (database is null) throw new InvalidOperationException("Credit notes require transactional Fabric persistence.");
        if (!BillingBalance.Label(command.Key) || command.Key.StartsWith("measured:", StringComparison.Ordinal)
            || command.Key.StartsWith("zarinpal:", StringComparison.Ordinal) || command.Key.StartsWith("organization:", StringComparison.Ordinal)
            || !BillingBalance.Label(actor) || !BillingBalance.Label(command.Reason, 1000) || !BillingBalance.Label(command.Reference, 1000)
            || command.ChargeId == Guid.Empty || command.Amount <= 0)
            throw new ArgumentException("A credit request key, charge, actor, positive amount, reason and reference are required.");
        var fingerprint = JsonSerializer.Serialize(command with { ExpectedRevision = 0 });
        return database.Transaction(() =>
        {
            lock (gate)
            {
                var view = Read(tenant); var account = view.Account;
                var receipt = account.Entries.SingleOrDefault(x => x.Kind == "receipt" && x.Key == command.Key);
                if (receipt is not null)
                {
                    if (receipt.Actor != actor || receipt.Reference != fingerprint) throw new InvalidOperationException("Credit request identity conflicts with its original actor or payload.");
                    return view;
                }
                if (account.Revision != command.ExpectedRevision) throw new InvalidOperationException("Account revision changed.");
                if (account.Entries.Any(x => x.Key == command.Key)) throw new InvalidOperationException("Credit request identity is occupied without its receipt.");
                if (!BillingBalance.ValidMoney(command.Amount, account.Currency)) throw new ArgumentException("Credit must retain account minor units.");
                var charge = account.Entries.SingleOrDefault(x => x.Kind == "usage" && x.Id == command.ChargeId) ?? throw new KeyNotFoundException("Posted charge not found.");
                var invoice = account.Invoices.SingleOrDefault(x => x.Period == BillingBalance.Period(charge.UsageOccurredAt ?? charge.At));
                if (command.InvoiceId != invoice?.Id) throw new ArgumentException("Credit must identify the charge's issued invoice, or no invoice for an unbilled charge.");
                var prior = BillingBalance.Sum(account.Entries.Where(x => x.Kind == "credit-note" && x.RelatedEntryId == charge.Id).Select(x => x.Amount));
                if (command.Amount > BillingBalance.Subtract(charge.Amount, prior)) throw new ArgumentException("Credit exceeds the charge's uncorrected amount.");
                if (account.Mode == "postpaid" && invoice is not null
                    && command.Amount > BillingBalance.Subtract(BillingBalance.InvoiceRemaining(account, invoice.Id), ReservedPayments(tenant, invoice.Id)))
                    throw new InvalidOperationException("Credit exceeds the invoice's unpaid and unreserved balance; refund or transfer is not supported.");
                var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
                var note = new LedgerEntry(Guid.NewGuid(), command.Key, "credit-note", charge.ProductKey, charge.Metric, 0, command.Amount,
                    now, actor, command.Reference, command.InvoiceId, RelatedEntryId: charge.Id, Reason: command.Reason);
                var marker = new LedgerEntry(Guid.NewGuid(), command.Key, "receipt", null, null, 0, 0, now, actor, fingerprint);
                account = account with { Revision = checked(account.Revision + 1), Entries = [.. account.Entries, note, marker] };
                BillingBalance.Validate(account, tenant);
                BillingBalance.EnsureWriteCapacity(account);
                var result = View(account); // Detect any exact-balance overflow before committing.
                database.Put("accounting", tenant.ToString(), account);
                database.Emit($"accounting:{tenant}:{account.Revision}", "AccountingChanged", new
                    { TenantId = tenant, account.Revision, Operation = "credit-note", command.Key, NoteId = note.Id, command.ChargeId,
                        command.InvoiceId, command.Amount, Actor = actor, command.Reason, command.Reference }, now);
                return result;
            }
        });
    }

    private Invoice CreateInvoice(BillingAccount account, string? period, DateTimeOffset now, string actor, string reference)
    {
        var (from, to) = BillingBalance.PeriodBounds(period);
        if (to > now) throw new ArgumentException("Only a completed UTC calendar month can be invoiced.");
        if (account.Invoices.Any(x => x.Period == period)) throw new InvalidOperationException("The monthly period is already closed.");
        if (account.Entries.Length > 10000) throw new InvalidOperationException("Invoice ledger scan bound exceeded.");
        EnsureNoPendingUsage(account, from, to);
        var lines = account.Entries.Where(x => x.Kind == "usage" && (x.UsageOccurredAt ?? x.At) >= from && (x.UsageOccurredAt ?? x.At) < to)
            .OrderBy(x => x.Id).Select(charge =>
            {
                var credit = BillingBalance.Sum(account.Entries.Where(x => x.Kind == "credit-note" && x.RelatedEntryId == charge.Id).Select(x => x.Amount));
                return new BillingInvoiceLine(charge.Id, charge.Key, charge.ProductKey!, charge.Metric!, charge.Quantity, charge.UnitPrice,
                    charge.Amount, credit, BillingBalance.Subtract(charge.Amount, credit), charge.UsageOccurredAt ?? charge.At, charge.At);
            }).ToArray();
        var invoice = new Invoice(Guid.NewGuid(), period!, BillingBalance.Sum(lines.Select(x => x.NetAmount)), now,
            account.Currency, from, to, lines, account.Revision, actor, reference);
        return invoice with { ContentHash = BillingBalance.InvoiceHash(invoice) };
    }
    private void EnsureNoPendingUsage(BillingAccount account, DateTimeOffset from, DateTimeOffset to)
    {
        if (database is null) return;
        var count = 0; string? after = null;
        var committed = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var rows = database.DocumentPage("measured.operation", after, 100);
            if (rows.Length == 0) break;
            foreach (var row in rows)
            {
                if (++count > 10000 || Encoding.UTF8.GetByteCount(row.Json) > 128 * 1024)
                    throw new InvalidOperationException("Invoice retained-usage scan bound exceeded.");
                using var json = BillingBalance.StrictJson(row.Json);
                BillingBalance.Require(json.RootElement, "BusinessKey", "Fingerprint", "EventJson", "Status", "Amount", "ReceivedAt", "CommittedAt");
                var operation = json.RootElement.Deserialize<MeasuredUsageOperation>() ?? throw new InvalidDataException("Invalid retained operation.");
                var usage = UsageEventV1.FromJson(operation.EventJson);
                if (usage.ConsumerTenantId != account.TenantId || usage.OccurredAt < from || usage.OccurredAt >= to) continue;
                if (operation.BusinessKey != row.Key || usage.BusinessKey != row.Key || operation.Fingerprint != usage.OperationFingerprint
                    || operation.Amount != usage.Amount || operation.Status != "committed" || operation.Error is not null || operation.CommittedAt is null
                    || operation.ReceivedAt.Offset != TimeSpan.Zero || operation.CommittedAt.Value.Offset != TimeSpan.Zero || operation.CommittedAt < operation.ReceivedAt
                    || !account.Entries.Any(x => x.Kind == "usage" && x.Key == row.Key && x.Amount == usage.Amount
                        && x.Quantity == usage.Quantity && x.UnitPrice == usage.UnitPrice && x.UsageOccurredAt == usage.OccurredAt
                        && x.Id == UsageEventV1.StableGuid(row.Key + ":charge") && x.ProductKey == usage.ProductKey && x.Metric == usage.Meter.Id
                        && x.Actor == usage.Operation.ActorId && x.Reference == usage.Evidence.Reference))
                    throw new InvalidOperationException("The period has pending or inconsistent retained usage; reconcile before closing it.");
                committed.Add(row.Key);
            }
            after = rows[^1].Key;
        }
        if (account.Entries.Any(x => x.Kind == "usage" && x.Key.StartsWith("measured:", StringComparison.Ordinal)
            && (x.UsageOccurredAt ?? x.At) >= from && (x.UsageOccurredAt ?? x.At) < to && !committed.Contains(x.Key)))
            throw new InvalidOperationException("A measured charge has no validated committed operation.");
    }
    private decimal ReservedPayments(Guid tenant, Guid invoiceId, string? ownKey = null)
        => database is null ? 0 : BillingBalance.Sum(database.All<PaymentIntent>("payment")
            .Where(x => x.TenantId == tenant && x.InvoiceId == invoiceId && ZarinpalPayments.ReservesBalance(x)
                && ownKey != "zarinpal:" + x.Id).Select(x => (decimal)x.Amount));
}
