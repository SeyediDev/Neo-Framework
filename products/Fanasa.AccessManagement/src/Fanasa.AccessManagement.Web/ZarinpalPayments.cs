using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Payments;

public sealed class ZarinpalOptions
{
    public bool Enabled { get; set; }
    public bool Sandbox { get; set; } = true;
    public string MerchantId { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public long MinimumAmount { get; set; } = 1000;
    public long MaximumAmount { get; set; } = 1_000_000_000;
    public void Validate()
    {
        if (!Enabled) throw new InvalidOperationException("درگاه زرین‌پال فعال نیست.");
        if (!Guid.TryParse(MerchantId, out var merchant) || merchant == Guid.Empty
            || !Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var callback) || callback.Scheme != "https"
            || callback.AbsolutePath != "/api/payments/zarinpal/callback" || callback.Query.Length != 0 || callback.Fragment.Length != 0
            || MinimumAmount < 1 || MaximumAmount < MinimumAmount)
            throw new InvalidOperationException("تنظیمات درگاه زرین‌پال معتبر نیست.");
    }
}

public sealed record PaymentIntent(Guid Id, Guid TenantId, string Actor, string RequestId, long Amount,
    Guid? InvoiceId, string Mode, bool Sandbox, string MerchantId, string CallbackUrl, string State,
    string? Authority, long? RefId, DateTimeOffset CreatedAt, string? ProviderStatus = null, DateTimeOffset? CheckedAt = null);
public sealed record GatewayRequest(string Authority);
public sealed record GatewayVerification(int Code, long RefId);
public sealed record GatewayInquiry(string Status);
public interface IZarinpalGateway
{
    Task<GatewayRequest> Request(PaymentIntent intent, CancellationToken cancellationToken);
    Task<GatewayVerification> Verify(PaymentIntent intent, CancellationToken cancellationToken);
    Task<GatewayInquiry> Inquiry(PaymentIntent intent, CancellationToken cancellationToken);
}

public sealed class ZarinpalGateway(HttpClient client) : IZarinpalGateway
{
    private static string Host(bool sandbox) => sandbox ? "https://sandbox.zarinpal.com" : "https://payment.zarinpal.com";
    public static bool ValidAuthority(string? authority, bool sandbox) => authority is not null
        && Regex.IsMatch(authority, sandbox ? "^S[a-zA-Z0-9]{35}$" : "^A[a-zA-Z0-9]{35}$", RegexOptions.CultureInvariant);
    public static string PaymentUrl(PaymentIntent intent) => Host(intent.Sandbox) + "/pg/StartPay/" + intent.Authority;
    public async Task<GatewayRequest> Request(PaymentIntent intent, CancellationToken cancellationToken)
    {
        using var data = await Send(intent.Sandbox, "request", new {
            merchant_id = intent.MerchantId, amount = intent.Amount, currency = "IRR",
            callback_url = intent.CallbackUrl + "?paymentId=" + intent.Id,
            description = intent.InvoiceId.HasValue ? "تسویه صورتحساب خدمات فن‌آسا" : "افزایش اعتبار خدمات فن‌آسا",
            metadata = new { order_id = intent.Id.ToString() }
        }, cancellationToken);
        var result = Data(data);
        if (result.GetProperty("code").GetInt32() != 100) throw new InvalidOperationException("درخواست پرداخت پذیرفته نشد.");
        var authority = result.GetProperty("authority").GetString();
        if (!ValidAuthority(authority, intent.Sandbox)) throw new InvalidOperationException("شناسه درگاه معتبر نیست.");
        return new(authority!);
    }
    public async Task<GatewayVerification> Verify(PaymentIntent intent, CancellationToken cancellationToken)
    {
        using var data = await Send(intent.Sandbox, "verify", new { merchant_id = intent.MerchantId, amount = intent.Amount, authority = intent.Authority }, cancellationToken);
        var result = Data(data); var code = result.GetProperty("code").GetInt32();
        return new(code, result.TryGetProperty("ref_id", out var reference) && reference.TryGetInt64(out var id) ? id : 0);
    }
    public async Task<GatewayInquiry> Inquiry(PaymentIntent intent, CancellationToken cancellationToken)
    {
        using var document = await Send(intent.Sandbox, "inquiry", new { merchant_id = intent.MerchantId, authority = intent.Authority }, cancellationToken);
        var data = Data(document);
        if (data.GetProperty("code").GetInt32() != 100 || !data.TryGetProperty("status", out var value)) throw new InvalidOperationException("استعلام پرداخت موفق نبود.");
        var status = value.GetString();
        if (status is not ("VERIFIED" or "PAID" or "IN_BANK" or "FAILED" or "REVERSED")) throw new InvalidOperationException("وضعیت درگاه ناشناخته است.");
        return new(status);
    }
    private async Task<JsonDocument> Send(bool sandbox, string method, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(Host(sandbox) + "/pg/v4/payment/" + method + ".json", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
    private static JsonElement Data(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("code", out _))
            throw new InvalidOperationException("پاسخ درگاه پرداخت معتبر نیست.");
        return data;
    }
}

public sealed class ZarinpalPayments(FabricDatabase database, AccountingStore accounting, IZarinpalGateway gateway, ZarinpalOptions options)
{
    public bool Enabled => options.Enabled;
    public object[] List(Guid tenant) => database.All<PaymentIntent>("payment").Where(x => x.TenantId == tenant)
        .OrderByDescending(x => x.CreatedAt).Take(50).Select(x => (object)new { x.Id, x.Amount, x.InvoiceId, x.State, x.RefId, x.Sandbox, x.CreatedAt, x.ProviderStatus, x.CheckedAt }).ToArray();
    public static bool ReservesBalance(PaymentIntent intent) => intent.State is "requesting" or "pending" or "verified";
    public PaymentIntent Get(Guid id) => database.Read<PaymentIntent>("payment", id.ToString()) ?? throw new KeyNotFoundException();
    public async Task<PaymentIntent> Begin(Guid tenant, string actor, string requestId, decimal amount, Guid? invoiceId, CancellationToken cancellationToken)
    {
        options.Validate();
        if (!ReferenceEquals(accounting.Database, database)) throw new InvalidOperationException("درگاه و دفتر حساب باید پایگاه تراکنشی مشترک داشته باشند.");
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100
            || amount != decimal.Truncate(amount) || amount < options.MinimumAmount || amount > options.MaximumAmount)
            throw new ArgumentException("مبلغ صحیح ریالی و شناسه درخواست معتبر الزامی است.");
        var created = false;
        var intent = database.Transaction(() => {
            var existing = database.All<PaymentIntent>("payment").SingleOrDefault(x => x.TenantId == tenant && x.RequestId == requestId);
            if (existing is not null)
            {
                if (existing.Actor != actor || existing.Amount != amount || existing.InvoiceId != invoiceId) throw new InvalidOperationException("شناسه درخواست با اطلاعات متفاوت استفاده شده است.");
                return existing;
            }
            var account = accounting.Read(tenant).Account;
            if (account.Currency != "IRR") throw new ArgumentException("زرین‌پال فقط برای حساب ریالی فعال است.");
            if (account.Mode == "postpaid")
            {
                var invoice = account.Invoices.SingleOrDefault(x => x.Id == invoiceId) ?? throw new ArgumentException("صورتحساب معتبر انتخاب کنید.");
                var paid = account.Entries.Where(x => x.Kind == "payment" && x.InvoiceId == invoiceId).Sum(x => x.Amount);
                var reserved = database.All<PaymentIntent>("payment").Where(x => x.TenantId == tenant && x.InvoiceId == invoiceId && ReservesBalance(x)).Sum(x => (decimal)x.Amount);
                if (amount > invoice.Amount - paid - reserved) throw new InvalidOperationException("مبلغ از مانده آزاد صورتحساب بیشتر است؛ درخواست پرداخت باز را بررسی کنید.");
            }
            else if (invoiceId.HasValue) throw new ArgumentException("شارژ کیف اعتبار به صورتحساب نیاز ندارد.");
            var next = new PaymentIntent(Guid.NewGuid(), tenant, actor, requestId, checked((long)amount), invoiceId,
                account.Mode, options.Sandbox, options.MerchantId, options.CallbackUrl, "requesting", null, null, DateTimeOffset.UtcNow);
            database.Put("payment", next.Id.ToString(), next); created = true; return next;
        });
        // Never retry an uncertain remote request automatically: it may already have an authority.
        if (!created) return intent;
        var result = await gateway.Request(intent, cancellationToken);
        if (!ZarinpalGateway.ValidAuthority(result.Authority, intent.Sandbox)) throw new InvalidOperationException("شناسه درگاه معتبر نیست.");
        return database.Transaction(() => {
            if (database.All<PaymentIntent>("payment").Any(x => x.Authority == result.Authority)) throw new InvalidOperationException("شناسه پرداخت تکراری است.");
            var ready = intent with { Authority = result.Authority, State = "pending" };
            database.Put("payment", intent.Id.ToString(), ready); return ready;
        });
    }
    public async Task<PaymentIntent> Complete(Guid id, string authority, string status, CancellationToken cancellationToken)
    {
        var intent = Get(id);
        if (intent.Authority != authority || !ZarinpalGateway.ValidAuthority(authority, intent.Sandbox)) throw new ArgumentException("بازگشت پرداخت معتبر نیست.");
        if (intent.State == "paid" || status != "OK") return intent;
        if (intent.State == "reversed") throw new InvalidOperationException("این پرداخت برگشت خورده است.");
        var verification = intent.RefId.HasValue ? new GatewayVerification(101, intent.RefId.Value) : await gateway.Verify(intent, cancellationToken);
        if (verification.Code is not (100 or 101) || verification.RefId <= 0) throw new InvalidOperationException("پرداخت توسط زرین‌پال تأیید نشد.");
        // Persist verification before applying the ledger, so local failures can be reconciled.
        database.Transaction(() => {
            var current = Get(id);
            if (current.State == "reversed") throw new InvalidOperationException("این پرداخت برگشت خورده است.");
            if (current.RefId.HasValue && current.RefId != verification.RefId) throw new InvalidOperationException("مرجع پرداخت ناسازگار است.");
            if (database.All<PaymentIntent>("payment").Any(x => x.Id != id && x.Sandbox == intent.Sandbox && x.RefId == verification.RefId)) throw new InvalidOperationException("مرجع پرداخت تکراری است.");
            if (current.State != "paid") database.Put("payment", id.ToString(), current with { RefId = verification.RefId, State = "verified" });
            return true;
        });
        return database.Transaction(() => {
            var current = Get(id); if (current.State == "paid") return current;
            var account = accounting.Read(current.TenantId).Account;
            if (account.Currency != "IRR" || account.Mode != current.Mode) throw new InvalidOperationException("حساب تغییر کرده؛ پرداخت تأییدشده نیازمند تطبیق است.");
            accounting.Execute(current.TenantId, new(account.Revision, current.Mode == "payg" ? "credit" : "payment",
                "zarinpal:" + current.Id, null, null, 0, null, null, 0, 0, current.Amount,
                "zarinpal:" + (current.Sandbox ? "sandbox:" : "live:") + verification.RefId, null, current.InvoiceId), current.Actor);
            var paid = current with { State = "paid" };
            database.Put("payment", id.ToString(), paid);
            database.Emit("payment:" + id, "PaymentApplied", new { current.Id, current.TenantId, current.Amount, current.InvoiceId, current.RefId, current.Sandbox }, DateTimeOffset.UtcNow);
            return paid;
        });
    }
    public async Task<PaymentIntent> Reconcile(Guid tenant, Guid id, string actor, CancellationToken cancellationToken)
    {
        var intent = Get(id);
        if (intent.TenantId != tenant) throw new KeyNotFoundException();
        if (intent.Authority is null) throw new InvalidOperationException("شناسه درگاه ذخیره نشده؛ تطبیق با گزارش پذیرنده لازم است.");
        var result = await gateway.Inquiry(intent, cancellationToken);
        if (result.Status is not ("VERIFIED" or "PAID" or "IN_BANK" or "FAILED" or "REVERSED")) throw new InvalidOperationException("وضعیت درگاه ناشناخته است.");
        intent = database.Transaction(() => {
            var current = Get(id); var now = DateTimeOffset.UtcNow;
            // FAILED may be retried at the bank. Only a confirmed reversal can release an unpaid reservation.
            var state = result.Status == "REVERSED" && current.RefId is null && current.State == "pending" ? "reversed" : current.State;
            var reviewed = current with { ProviderStatus = result.Status, CheckedAt = now, State = state };
            database.Put("payment", id.ToString(), reviewed);
            database.Emit("payment-review:" + Guid.NewGuid(), "PaymentReviewed", new { current.Id, current.TenantId, Actor = actor, Status = result.Status, State = state }, now);
            return reviewed;
        });
        // Inquiry is evidence only; a paid response must still pass the amount-bound verification.
        return result.Status is "PAID" or "VERIFIED" && intent.State != "reversed"
            ? await Complete(id, intent.Authority!, "OK", cancellationToken) : intent;
    }
}
