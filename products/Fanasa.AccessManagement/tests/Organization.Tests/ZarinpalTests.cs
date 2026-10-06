using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Payments;
using Fanasa.AccessManagement.Web.Persistence;
using System.Net;
using System.Text;
using System.Text.Json;

static class ZarinpalTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        void Reject<T>(Action action, string label) where T : Exception { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        using var database = new FabricDatabase(Path.Combine(directory, "payments.db"));
        var access = new InMemoryAccessManagement(); var tenant = access.GetTenants().Single().Id;
        var billing = new AccountingStore(directory, access, database: database);
        var options = new ZarinpalOptions { Enabled = true, Sandbox = true, MerchantId = Guid.NewGuid().ToString(), CallbackUrl = "https://access.example/api/payments/zarinpal/callback" };
        var fake = new Gateway(); var service = new ZarinpalPayments(database, billing, fake, options);
        PaymentIntent Begin(string key, decimal amount = 1000, Guid? invoice = null) => service.Begin(tenant, "payer", key, amount, invoice, default).GetAwaiter().GetResult();
        PaymentIntent Complete(PaymentIntent intent, string status = "OK") => service.Complete(intent.Id, intent.Authority!, status, default).GetAwaiter().GetResult();
        var first = Begin("one");
        Reject<InvalidOperationException>(() => billing.Execute(tenant, new(0, "configure", "change-pending", "postpaid", "IRR", 10000, null, null, 0, 0, 0, "test", null, null), "test"), "pending gateway payment prevents account mode change");
        check(Begin("one").Id == first.Id && fake.Requests == 1, "gateway request retry reuses persisted authority");
        Reject<InvalidOperationException>(() => Begin("one", 2000), "gateway request payload conflict rejected");
        Reject<ArgumentException>(() => Begin("fraction", 1000.5m), "gateway only accepts integer rial amounts");
        Reject<ArgumentException>(() => service.Complete(first.Id, "S" + new string('9', 35), "OK", default).GetAwaiter().GetResult(), "forged callback authority rejected");
        check(Complete(first, "NOK").State == "pending" && fake.Verifies == 0 && billing.Read(tenant).WalletBalance == 0, "cancel callback cannot credit or release unverified payment");
        fake.Code = -21;
        Reject<InvalidOperationException>(() => Complete(first), "unsuccessful verification cannot credit wallet");
        fake.Code = 100;
        var paid = Complete(first);
        check(paid.State == "paid" && billing.Read(tenant).WalletBalance == 1000, "server verified payment credits wallet");
        var verifies = fake.Verifies;
        check(Complete(first).State == "paid" && fake.Verifies == verifies && billing.Read(tenant).WalletBalance == 1000, "replayed callback applies ledger exactly once");
        var next = Begin("two"); fake.Code = 101;
        check(Complete(next).State == "paid" && billing.Read(tenant).WalletBalance == 2000, "already verified gateway response safely recovers payment");
        var duplicate = Begin("duplicate"); fake.RepeatReference = paid.RefId;
        Reject<InvalidOperationException>(() => Complete(duplicate), "gateway reference cannot credit two intents"); fake.RepeatReference = null;
        fake.FailRequest = true;
        Reject<HttpRequestException>(() => Begin("unknown"), "uncertain gateway request remains durable");
        var requests = fake.Requests;
        check(Begin("unknown").State == "requesting" && fake.Requests == requests, "uncertain request retry cannot create duplicate remote payment"); fake.FailRequest = false;
        var other = access.CreateTenant("post", "Post").Id;
        var clock = new Clock(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var postBilling = new AccountingStore(directory, access, clock, database);
        access.RegisterProduct(new("meter", "Meter", "meter", null, "fanasa.ayin", null));
        BillingCommand B(string op, string key) => new(postBilling.Read(other).Account.Revision, op, key, "postpaid", "IRR", 10000, "meter", "item", 3000, 1, 0, "test", "2026-09", null);
        postBilling.Execute(other, B("configure", "config"), "test"); postBilling.Execute(other, B("tariff", "rate"), "test"); postBilling.Execute(other, B("usage", "usage"), "test");
        clock.Now = clock.Now.AddMonths(1); var invoice = postBilling.Execute(other, B("invoice", "invoice"), "test").Account.Invoices.Single();
        var post = new ZarinpalPayments(database, postBilling, fake, options);
        PaymentIntent Post(string key, decimal amount) => post.Begin(other, "payer", key, amount, invoice.Id, default).GetAwaiter().GetResult();
        var installment = Post("partial", 1000);
        var cancelled = Post("cancelled", 2000);
        fake.Status = "FAILED";
        check(post.Reconcile(other, cancelled.Id, "finance", default).GetAwaiter().GetResult().State == "pending", "failed inquiry preserves pending invoice reservation");
        Reject<InvalidOperationException>(() => Post("failed-reservation", 1000), "failed inquiry cannot make pending funds available");
        fake.Status = "REVERSED";
        check(post.Reconcile(other, cancelled.Id, "finance", default).GetAwaiter().GetResult().State == "reversed" && !ZarinpalPayments.ReservesBalance(post.Get(cancelled.Id)), "confirmed reversal releases only unpaid reservation");
        Reject<InvalidOperationException>(() => post.Complete(cancelled.Id, cancelled.Authority!, "OK", default).GetAwaiter().GetResult(), "reversed payment cannot be credited by replay");
        Reject<InvalidOperationException>(() => Post("over-reserved", 3000), "postpaid pending payments reserve invoice balance");
        post.Complete(installment.Id, installment.Authority!, "OK", default).GetAwaiter().GetResult();
        check(postBilling.Read(other).Outstanding == 2000, "gateway partial postpaid settlement restores credit");
        var conflict = Post("local-failure", 2000);
        postBilling.Execute(other, B("payment", "manual") with { Amount = 2000, InvoiceId = invoice.Id }, "test");
        Reject<ArgumentException>(() => post.Complete(conflict.Id, conflict.Authority!, "OK", default).GetAwaiter().GetResult(), "local settlement conflict does not lose verified payment");
        check(post.Get(conflict.Id).State == "verified" && post.Get(conflict.Id).RefId.HasValue, "verified unapplied payment retained for reconciliation");
        check(post.Reconcile(other, conflict.Id, "finance", default).GetAwaiter().GetResult().State == "verified" && ZarinpalPayments.ReservesBalance(post.Get(conflict.Id)), "reversal of verified payment requires financial review without silent release");
        check(service.Reconcile(tenant, first.Id, "finance", default).GetAwaiter().GetResult().State == "paid" && billing.Read(tenant).WalletBalance == 2000, "reversal after ledger credit cannot silently rewrite wallet");
        Reject<KeyNotFoundException>(() => service.Reconcile(other, first.Id, "finance", default).GetAwaiter().GetResult(), "payment reconciliation rejects foreign tenant");
        var inquiryPayment = Begin("inquiry"); fake.Status = "IN_BANK"; verifies = fake.Verifies;
        check(service.Reconcile(tenant, inquiryPayment.Id, "finance", default).GetAwaiter().GetResult().State == "pending" && fake.Verifies == verifies, "in-bank inquiry cannot credit wallet");
        fake.Status = "PAID"; fake.Code = -21;
        Reject<InvalidOperationException>(() => service.Reconcile(tenant, inquiryPayment.Id, "finance", default).GetAwaiter().GetResult(), "paid inquiry still requires successful amount-bound verify");
        check(billing.Read(tenant).WalletBalance == 2000, "inquiry alone never credits account");
        fake.Code = 100;
        check(service.Reconcile(tenant, inquiryPayment.Id, "finance", default).GetAwaiter().GetResult().State == "paid" && billing.Read(tenant).WalletBalance == 3000, "reconciliation recovers paid payment through verify");
        fake.Status = "UNKNOWN";
        Reject<InvalidOperationException>(() => service.Reconcile(tenant, first.Id, "finance", default).GetAwaiter().GetResult(), "unknown provider status cannot change payment state");
        using var reopened = new FabricDatabase(Path.Combine(directory, "payments.db"));
        check(reopened.Read<PaymentIntent>("payment", paid.Id.ToString())!.State == "paid", "gateway payment state survives restart");
        var http = new Capture(); var wire = new ZarinpalGateway(new HttpClient(http));
        wire.Request(first, default).GetAwaiter().GetResult();
        using var payload = JsonDocument.Parse(http.Body!);
        check(http.Url == "https://sandbox.zarinpal.com/pg/v4/payment/request.json" && payload.RootElement.GetProperty("amount").GetInt64() == 1000 && payload.RootElement.GetProperty("currency").GetString() == "IRR", "gateway wire contract uses sandbox integer rial amount");
        check(payload.RootElement.GetProperty("callback_url").GetString()!.EndsWith("?paymentId=" + first.Id), "gateway callback binds persisted payment identity");
        http.ResponseBody = "{\"data\":{\"code\":100,\"status\":\"PAID\"},\"errors\":[]}";
        var wireInquiry = wire.Inquiry(first, default).GetAwaiter().GetResult();
        using var inquiryPayload = JsonDocument.Parse(http.Body!);
        check(wireInquiry.Status == "PAID" && http.Url == "https://sandbox.zarinpal.com/pg/v4/payment/inquiry.json" && inquiryPayload.RootElement.GetProperty("authority").GetString() == first.Authority, "inquiry wire contract binds stored merchant and authority");
    }
    sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    sealed class Gateway : IZarinpalGateway
    {
        public int Requests, Verifies, Code = 100; public bool FailRequest; public long? RepeatReference; public string Status = "IN_BANK";
        public Task<GatewayInquiry> Inquiry(PaymentIntent intent, CancellationToken cancellationToken) => Task.FromResult(new GatewayInquiry(Status));
        public Task<GatewayRequest> Request(PaymentIntent intent, CancellationToken cancellationToken)
        { Requests++; if (FailRequest) throw new HttpRequestException(); return Task.FromResult(new GatewayRequest("S" + Requests.ToString("D35"))); }
        public Task<GatewayVerification> Verify(PaymentIntent intent, CancellationToken cancellationToken)
        { Verifies++; return Task.FromResult(new GatewayVerification(Code, RepeatReference ?? 1000 + Verifies)); }
    }
    sealed class Capture : HttpMessageHandler
    {
        public string? Url, Body;
        public string ResponseBody = "{\"data\":{\"code\":100,\"authority\":\"S00000000000000000000000000000000001\"},\"errors\":[]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Url = request.RequestUri!.ToString(); Body = await request.Content!.ReadAsStringAsync(cancellationToken); return new(HttpStatusCode.OK) { Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json") }; }
    }
}
