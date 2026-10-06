using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;

namespace Fanasa.AccessManagement.Web.Accounting;

public sealed record Tariff(string ProductKey, string Metric, decimal UnitPrice);
public sealed record LedgerEntry(Guid Id, string Key, string Kind, string? ProductKey, string? Metric, decimal Quantity, decimal Amount, DateTimeOffset At, string Actor, string Reference, Guid? InvoiceId = null);
public sealed record Invoice(Guid Id, string Period, decimal Amount, DateTimeOffset IssuedAt);
public sealed record BillingAccount(Guid TenantId, long Revision, string Mode, string Currency, decimal CreditLimit, Tariff[] Tariffs, LedgerEntry[] Entries, Invoice[] Invoices);
public sealed record BillingCommand(long ExpectedRevision, string Operation, string Key, string? Mode, string? Currency, decimal CreditLimit, string? ProductKey, string? Metric, decimal UnitPrice, decimal Quantity, decimal Amount, string Reference, string? Period, Guid? InvoiceId);
public sealed record AccountView(BillingAccount Account, decimal WalletBalance, decimal Outstanding, decimal AvailableCredit);

// Durable single-host ledger; no gateway or general-ledger integration is implied.
public sealed class AccountingStore(string directory, IAccessManagement access, TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private string PathFor(Guid tenant) => Path.Combine(Path.GetFullPath(directory), tenant + ".billing.json");
    public AccountView Read(Guid tenant)
    {
        lock (gate)
        {
            if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive)) throw new KeyNotFoundException();
            var path = PathFor(tenant);
            var account = File.Exists(path) ? JsonSerializer.Deserialize<BillingAccount>(File.ReadAllText(path)) ?? throw new InvalidDataException() : new(tenant, 0, "payg", "IRR", 0, [], [], []);
            return View(account);
        }
    }
    public AccountView Execute(Guid tenant, BillingCommand command, string actor)
    {
        lock (gate)
        {
            var view = Read(tenant); var account = view.Account;
            if (string.IsNullOrWhiteSpace(command.Key) || command.Key.Length > 200 || string.IsNullOrWhiteSpace(command.Reference) || command.Reference.Length > 1000 || string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("کلید یکتا، عامل و مرجع الزامی است.");
            // Store the exact request without its concurrency token for safe retries.
            var fingerprint = JsonSerializer.Serialize(command with { ExpectedRevision = 0 });
            var receipts = account.Entries.Where(x => x.Kind == "receipt").ToDictionary(x => x.Key, x => x.Reference);
            if (receipts.TryGetValue(command.Key, out var previous))
            { if (previous != fingerprint) throw new InvalidOperationException("کلید تکراری با درخواست متفاوت."); return view; }
            if (account.Revision != command.ExpectedRevision) throw new InvalidOperationException("حساب تغییر کرده است؛ دوباره بارگذاری کنید.");
            var tariffs = account.Tariffs.ToList(); var entries = account.Entries.ToList(); var invoices = account.Invoices.ToList();
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            switch (command.Operation)
            {
                case "configure":
                    if (command.Mode is not ("payg" or "postpaid") || command.Currency is not ("IRR" or "USD" or "EUR") || command.CreditLimit < 0) throw new ArgumentException("مدل، ارز یا سقف اعتبار معتبر نیست.");
                    if (account.Entries.Any(x => x.Kind != "receipt") && (command.Currency != account.Currency || command.Mode != account.Mode)) throw new ArgumentException("مدل یا ارز حساب دارای تراکنش قابل تغییر نیست.");
                    if (command.Mode == "postpaid" && command.CreditLimit < view.Outstanding) throw new ArgumentException("سقف اعتبار از بدهی فعلی کمتر است.");
                    account = account with { Mode = command.Mode, Currency = command.Currency, CreditLimit = command.CreditLimit };
                    break;
                case "tariff":
                    if (string.IsNullOrWhiteSpace(command.ProductKey) || !access.GetProducts().Any(x => x.Key == command.ProductKey) || string.IsNullOrWhiteSpace(command.Metric) || command.Metric.Length > 100 || command.UnitPrice < 0) throw new ArgumentException("محصول ثبت‌شده، شاخص و تعرفه معتبر الزامی است.");
                    tariffs.RemoveAll(x => x.ProductKey == command.ProductKey && x.Metric == command.Metric);
                    tariffs.Add(new(command.ProductKey, command.Metric, command.UnitPrice));
                    break;
                case "usage":
                    if (command.Quantity <= 0) throw new ArgumentException("مقدار مصرف باید مثبت باشد.");
                    var tariff = tariffs.SingleOrDefault(x => x.ProductKey == command.ProductKey && x.Metric == command.Metric) ?? throw new ArgumentException("تعرفه شاخص ثبت نشده است.");
                    var cost = decimal.Round(checked(command.Quantity * tariff.UnitPrice), account.Currency == "IRR" ? 0 : 2, MidpointRounding.AwayFromZero);
                    if (account.Mode == "payg" && cost > view.WalletBalance) throw new InvalidOperationException("موجودی برای مصرف کافی نیست.");
                    if (account.Mode == "postpaid" && cost > view.AvailableCredit) throw new InvalidOperationException("سقف اعتبار پس‌پرداخت کافی نیست.");
                    entries.Add(new(Guid.NewGuid(), command.Key, "usage", command.ProductKey, command.Metric, command.Quantity, cost, now, actor, command.Reference));
                    break;
                case "credit":
                    if (account.Mode != "payg" || command.Amount <= 0 || decimal.Round(command.Amount, account.Currency == "IRR" ? 0 : 2) != command.Amount) throw new ArgumentException("شارژ تأییدشده فقط برای حساب مصرفی و مبلغ مثبت مجاز است.");
                    entries.Add(new(Guid.NewGuid(), command.Key, "credit", null, null, 0, command.Amount, now, actor, command.Reference));
                    break;
                case "invoice":
                    if (!DateTimeOffset.TryParseExact(command.Period + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var start) || start >= new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)) throw new ArgumentException("فقط دوره ماهانه پایان‌یافته قابل صدور است.");
                    if (invoices.Any(x => x.Period == command.Period)) throw new InvalidOperationException("صورتحساب این دوره صادر شده است.");
                    var amount = entries.Where(x => x.Kind == "usage" && x.At >= start && x.At < start.AddMonths(1)).Sum(x => x.Amount);
                    invoices.Add(new(Guid.NewGuid(), command.Period!, amount, now));
                    break;
                case "payment":
                    if (account.Mode != "postpaid") throw new ArgumentException("تسویه صورتحساب برای پس‌پرداخت است.");
                    var invoice = invoices.SingleOrDefault(x => x.Id == command.InvoiceId) ?? throw new KeyNotFoundException();
                    var remaining = invoice.Amount - entries.Where(x => x.Kind == "payment" && x.InvoiceId == invoice.Id).Sum(x => x.Amount);
                    if (command.Amount <= 0 || command.Amount > remaining || decimal.Round(command.Amount, account.Currency == "IRR" ? 0 : 2) != command.Amount) throw new ArgumentException("مبلغ تسویه معتبر نیست.");
                    entries.Add(new(Guid.NewGuid(), command.Key, "payment", null, null, 0, command.Amount, now, actor, command.Reference, invoice.Id));
                    break;
                default: throw new ArgumentException("عملیات حسابداری ناشناخته است.");
            }
            entries.Add(new(Guid.NewGuid(), command.Key, "receipt", null, null, 0, 0, now, actor, fingerprint));
            account = account with { Revision = account.Revision + 1, Tariffs = tariffs.ToArray(), Entries = entries.ToArray(), Invoices = invoices.ToArray() };
            var path = PathFor(tenant); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid() + ".tmp";
            try { using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, account); stream.Flush(true); } File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return View(account);
        }
    }
    private static AccountView View(BillingAccount account)
    {
        var usage = account.Entries.Where(x => x.Kind == "usage").Sum(x => x.Amount);
        var credits = account.Entries.Where(x => x.Kind == "credit").Sum(x => x.Amount);
        var paid = account.Entries.Where(x => x.Kind == "payment").Sum(x => x.Amount);
        var debt = account.Mode == "postpaid" ? usage - paid : 0;
        return new(account, account.Mode == "payg" ? credits - usage : 0, debt, account.Mode == "postpaid" ? account.CreditLimit - debt : 0);
    }
}
