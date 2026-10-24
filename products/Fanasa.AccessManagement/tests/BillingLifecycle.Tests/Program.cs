using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Payments;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.Data.Sqlite;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++; Console.WriteLine("PASS Billing lifecycle: " + name);
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("one", 3); f.Use("two", 2);
    Reject<ArgumentException>(() => f.Invoice("early"), "open UTC month cannot close");
    f.Clock.Now = Fixture.Start.AddMonths(1);
    var command = new BillingInvoiceCommand(f.Read().Account.Revision, "invoice-once", "2026-09", "approval/one");
    var issued = f.Billing.IssueInvoice(f.Tenant, command, "finance"); var invoice = issued.Account.Invoices.Single();
    Check(invoice.Amount == 50 && invoice.Currency == "IRR" && invoice.From == Fixture.Start && invoice.To == Fixture.Start.AddMonths(1), "invoice freezes exact amount currency and half-open UTC month");
    Check(invoice.Lines is { Length: 2 } && invoice.Lines.All(x => x.UnitPrice == 10 && x.NetAmount == x.GrossAmount && x.CreditAmount == 0), "invoice freezes every charge and historical unit price");
    Check(invoice.SourceRevision == command.ExpectedRevision && invoice.Actor == "finance" && invoice.Reference == "approval/one" && invoice.ContentHash is { Length: 64 }, "invoice retains source revision actor reference and integrity hash");
    var snapshot = f.Snapshot();
    Check(f.Billing.IssueInvoice(f.Tenant, command with { ExpectedRevision = 0 }, "finance").Account.Invoices.Single().Id == invoice.Id && f.Snapshot() == snapshot, "same invoice request replays without writes after revision changed");
    Reject<InvalidOperationException>(() => f.Billing.IssueInvoice(f.Tenant, command, "other-actor"), "invoice retry is actor-bound");
    Reject<InvalidOperationException>(() => f.Billing.IssueInvoice(f.Tenant, command with { Reference = "changed" }, "finance"), "invoice request cannot replace approved payload");
    Reject<InvalidOperationException>(() => f.Invoice("another-identity"), "new request cannot issue the same period twice");
    Check(f.Snapshot() == snapshot, "rejected invoice commands preserve ledger and outbox");
    f.Rate(90);
    Check(JsonSerializer.Serialize(f.Read().Account.Invoices.Single()) == JsonSerializer.Serialize(invoice), "later tariff change cannot rewrite invoice lines");
    var charge = f.Read().Account.Entries.First(x => x.Kind == "usage");
    var credit = new BillingCreditNoteCommand(f.Read().Account.Revision, "credit-once", charge.Id, invoice.Id, 12, "approved service correction", "case/17");
    f.Billing.ApplyCreditNote(f.Tenant, credit, "finance");
    Check(f.Read().Outstanding == 38 && f.Read().AvailableCredit == 999962, "postpaid correction restores only local credit");
    Check(BillingBalance.InvoiceCredits(f.Read().Account, invoice.Id) == 12 && BillingBalance.InvoiceRemaining(f.Read().Account, invoice.Id) == 38, "invoice remaining accounts for distinct post-issue credit note");
    Check(JsonSerializer.Serialize(f.Read().Account.Invoices.Single()) == JsonSerializer.Serialize(invoice), "post-issue credit leaves original invoice immutable");
    var note = f.Read().Account.Entries.Single(x => x.Kind == "credit-note");
    Check(note.RelatedEntryId == charge.Id && note.InvoiceId == invoice.Id && note.Actor == "finance" && note.Reason == credit.Reason && note.Reference == credit.Reference, "credit ledger row retains charge invoice actor reason and reference");
    snapshot = f.Snapshot(); f.Billing.ApplyCreditNote(f.Tenant, credit with { ExpectedRevision = 0 }, "finance");
    Check(f.Snapshot() == snapshot, "credit request replay appends neither note nor audit event");
    Reject<InvalidOperationException>(() => f.Billing.ApplyCreditNote(f.Tenant, credit, "other-actor"), "credit retry is actor-bound");
    Reject<InvalidOperationException>(() => f.Billing.ApplyCreditNote(f.Tenant, credit with { Amount = 1 }, "finance"), "credit retry cannot change its amount");
    Reject<ArgumentException>(() => f.Credit("over-charge", charge.Id, 19, invoice.Id), "cumulative credit cannot exceed the target charge");
    Reject<ArgumentException>(() => f.Credit("wrong-invoice", charge.Id, 1, Guid.NewGuid()), "credit cannot target an unrelated invoice");
    Reject<ArgumentException>(() => f.Credit("missing-invoice", charge.Id, 1), "invoiced charge requires explicit matching invoice");
    Reject<KeyNotFoundException>(() => f.Credit("missing-charge", Guid.NewGuid(), 1), "credit requires a posted charge in this account");
    Reject<ArgumentException>(() => f.Credit("fraction-irr", charge.Id, .5m, invoice.Id), "credit obeys account currency minor units");
    f.Pay("partial", invoice.Id, 30);
    Check(f.Read().Outstanding == 8 && BillingBalance.InvoicePaid(f.Read().Account, invoice.Id) == 30, "partial payment and correction preserve exact invoice balance");
    Reject<InvalidOperationException>(() => f.Credit("over-unpaid", charge.Id, 9, invoice.Id), "correction cannot create refundable or transferred postpaid balance");
    f.Pay("settle", invoice.Id, 8);
    Reject<InvalidOperationException>(() => f.Credit("settled-note", charge.Id, 1, invoice.Id), "settled invoice correction requires a separate refund workflow");
    Check(f.Read().Outstanding == 0 && BillingBalance.InvoiceRemaining(f.Read().Account, invoice.Id) == 0, "settled local invoice remains zero");
    var restarted = new AccountingStore(f.Root, f.Access, f.Clock, f.Database);
    Check(JsonSerializer.Serialize(restarted.Read(f.Tenant)) == JsonSerializer.Serialize(f.Read()), "invoice note and payment state survive reopening store");
    f.Clock.Now = Fixture.Start.AddDays(3);
    Reject<InvalidOperationException>(() => f.Use("rewound-clock"), "legacy usage cannot append to an already issued period after clock rewind");
}

using (var f = new Fixture())
{
    f.Configure("payg"); f.Fund(100); f.Rate(10); f.Use("payg", 3);
    var charge = f.Read().Account.Entries.Single(x => x.Kind == "usage"); f.Credit("preinvoice-note", charge.Id, 7);
    Check(f.Read().WalletBalance == 77 && f.Read().Outstanding == 0, "PAYG note restores local wallet without inventing payment or refund");
    f.Clock.Now = Fixture.Start.AddMonths(1); var invoice = f.Invoice("payg-statement").Account.Invoices.Single();
    Check(invoice.Amount == 23 && invoice.Lines!.Single().GrossAmount == 30 && invoice.Lines!.Single().CreditAmount == 7 && invoice.Lines!.Single().NetAmount == 23, "preissue correction is frozen once in invoice net amount");
    Check(BillingBalance.InvoiceCredits(f.Read().Account, invoice.Id) == 0 && BillingBalance.InvoiceRemaining(f.Read().Account, invoice.Id) == 23, "prior correction is not subtracted from invoice twice");
    f.Credit("postinvoice-payg-note", charge.Id, 3, invoice.Id);
    Check(f.Read().WalletBalance == 80 && f.Read().Account.Invoices.Single().Amount == 23 && BillingBalance.InvoiceRemaining(f.Read().Account, invoice.Id) == 20, "PAYG postissue correction retains original statement and restores wallet");
    Reject<ArgumentException>(() => f.Pay("payg-payment", invoice.Id, 1), "PAYG statement is not a postpaid payment target");
    Check(f.Read().Account.Entries.Count(x => x.Kind == "credit") == 1 && f.Read().Account.Entries.All(x => x.Kind != "payment"), "local corrections do not manufacture bank credit or settlement rows");
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(100); f.Use("reserved"); f.Clock.Now = Fixture.Start.AddMonths(1);
    var invoice = f.Invoice("reserved-invoice").Account.Invoices.Single(); var charge = f.Read().Account.Entries.Single(x => x.Kind == "usage");
    var intent = new PaymentIntent(Guid.NewGuid(), f.Tenant, "payer", "gateway-request", 80, invoice.Id,
        "postpaid", true, Guid.NewGuid().ToString(), "https://example.invalid/callback", "pending", "S" + new string('1', 35), null, f.Clock.Now);
    f.Database.Transaction(() => { f.Database.Put("payment", intent.Id.ToString(), intent); return true; });
    Reject<InvalidOperationException>(() => f.Credit("reservation-note", charge.Id, 21, invoice.Id), "credit cannot consume an open gateway reservation");
    Reject<ArgumentException>(() => f.Pay("manual-reserved", invoice.Id, 21), "manual payment cannot consume an open gateway reservation");
    f.Credit("free-note", charge.Id, 20, invoice.Id); f.Pay("zarinpal:" + intent.Id, invoice.Id, 80);
    Check(f.Read().Outstanding == 0 && BillingBalance.InvoiceRemaining(f.Read().Account, invoice.Id) == 0, "trusted gateway posting excludes its own reservation and respects approved credit");
}

using (var f = new Fixture())
{
    f.Configure(credit: 0); var usage = f.Event("pending"); var intake = new MeasuredUsageStore(f.Database, f.Billing, new Validator(), f.Clock);
    Check(intake.Accept(usage, true).Status == "accounting-pending", "unfunded completed usage retains pending evidence");
    f.Clock.Now = Fixture.Start.AddMonths(1); var before = f.Snapshot();
    Reject<InvalidOperationException>(() => f.Invoice("pending-close"), "known pending usage prevents period closure");
    Check(f.Snapshot() == before, "blocked closure leaves pending evidence recoverable and unchanged");
    f.Configure(credit: 100); intake.Accept(usage); var invoice = f.Invoice("after-recovery").Account.Invoices.Single();
    Check(invoice.Amount == 12 && invoice.Lines!.Single().OccurredAt == usage.OccurredAt && invoice.Lines!.Single().RecordedAt == f.Clock.Now, "late posting before closure is invoiced in its original occurrence month");
    var late = f.Event("after-closure"); before = JsonSerializer.Serialize(f.Read().Account);
    Check(intake.Accept(late, true).Status == "accounting-pending" && JsonSerializer.Serialize(f.Read().Account) == before, "closed-period late usage remains pending without rewriting invoice");
    Check(intake.Accept(usage).Status == "committed" && f.Read().Account.Entries.Count(x => x.Kind == "usage") == 1, "committed usage replay after closure remains idempotent");
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("bad-evidence"); f.Clock.Now = Fixture.Start.AddMonths(1);
    f.Database.Transaction(() => { f.Database.Put("measured.operation", "unattributable", new { Broken = true }); return true; });
    Reject<InvalidDataException>(() => f.Invoice("corrupt-evidence"), "unattributable malformed retained operation prevents false-complete closure");
    Check(f.Read().Account.Invoices.Length == 0, "invalid scan does not issue an invoice");
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("parallel", 2); f.Clock.Now = Fixture.Start.AddMonths(1);
    var revision = f.Read().Account.Revision; var passed = 0;
    Parallel.For(0, 2, index =>
    {
        var peer = new AccountingStore(f.Root, f.Access, f.Clock, f.Database);
        try { peer.IssueInvoice(f.Tenant, new(revision, "parallel-" + index, "2026-09", "approved"), "finance"); Interlocked.Increment(ref passed); }
        catch (InvalidOperationException) { }
    });
    Check(passed == 1 && f.Read().Account.Invoices.Length == 1, "concurrent invoice attempts serialize into one immutable period");
    var charge = f.Read().Account.Entries.Single(x => x.Kind == "usage"); var invoice = f.Read().Account.Invoices.Single(); revision = f.Read().Account.Revision; passed = 0;
    Parallel.For(0, 2, index =>
    {
        var peer = new AccountingStore(f.Root, f.Access, f.Clock, f.Database);
        try { peer.ApplyCreditNote(f.Tenant, new(revision, "parallel-note-" + index, charge.Id, invoice.Id, 15, "approval", "case"), "finance"); Interlocked.Increment(ref passed); }
        catch (InvalidOperationException) { }
    });
    Check(passed == 1 && f.Read().Account.Entries.Count(x => x.Kind == "credit-note") == 1 && f.Read().Outstanding == 5, "concurrent note requests cannot exceed charge or invoice balance");
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("rollback"); f.Clock.Now = Fixture.Start.AddMonths(1); var before = f.Snapshot();
    Reject<InvalidOperationException>(() => f.Database.Transaction<bool>(() => { f.Invoice("rolled-back"); throw new InvalidOperationException("crash"); }), "invoice transaction rolls back after a staged write");
    Check(f.Snapshot() == before, "invoice receipt document and outbox rollback together");
    var invoice = f.Invoice("rollback-closed").Account.Invoices.Single(); var charge = f.Read().Account.Entries.Single(x => x.Kind == "usage"); before = f.Snapshot();
    Reject<InvalidOperationException>(() => f.Database.Transaction<bool>(() => { f.Credit("rolled-note", charge.Id, 1, invoice.Id); throw new InvalidOperationException("crash"); }), "note transaction rolls back after a staged write");
    Check(f.Snapshot() == before, "note receipt ledger and audit rollback together");
    var account = f.Read().Account;
    f.Database.Transaction(() => { f.Database.Put("accounting", f.Tenant.ToString(), account with { TenantId = Guid.NewGuid() }); return true; });
    Reject<InvalidDataException>(() => f.Read(), "foreign account stored under requested key fails closed");
    f.Database.Transaction(() => { f.Database.Put("accounting", f.Tenant.ToString(), account with { Invoices = [invoice with { Amount = 99 }] }); return true; });
    Reject<InvalidDataException>(() => f.Read(), "modified immutable invoice amount or hash fails closed");
}

using (var f = new Fixture())
{
    f.Clock.Now = Fixture.Start.AddMonths(1); var invoice = f.Invoice("empty").Account.Invoices.Single();
    Check(invoice.Amount == 0 && invoice.Lines is { Length: 0 }, "zero closed period has an explicit immutable empty snapshot");
    Reject<ArgumentException>(() => f.Configure("postpaid", "USD"), "even an empty issued invoice freezes account currency and mode");
    Reject<ArgumentException>(() => f.Billing.IssueInvoice(f.Tenant, new(f.Read().Account.Revision, "bad-month", "2026-9", "ref"), "finance"), "period input must use canonical Gregorian year-month");
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("namespace-safe"); f.Clock.Now = Fixture.Start.AddMonths(1);
    var before = f.Snapshot(); var revision = f.Read().Account.Revision;
    foreach (var prefix in new[] { "measured:", "organization:", "zarinpal:", "billing-note:" })
    {
        Reject<ArgumentException>(() => f.Billing.IssueInvoice(f.Tenant, new(revision, prefix + Guid.NewGuid(), "2026-09", "approval"), "finance"),
            "public invoice request rejects protected namespace " + prefix);
        Check(f.Snapshot() == before, "protected invoice key appends no receipt invoice or outbox for " + prefix);
    }
}

using (var f = new Fixture())
{
    f.Configure(); f.Rate(10); f.Use("precision"); var account = f.Read().Account;
    var json = JsonSerializer.Serialize(account);
    f.RawAccount(json.Replace("\"Amount\":10,", "\"Amount\":10.00000000000000000000000000001,", StringComparison.Ordinal));
    Reject<InvalidDataException>(() => f.Read(), "persisted financial token with a discarded nonzero decimal tail is rejected");
    f.RawAccount(json.Replace("\"Revision\":", "\"Revision\":0,\"Revision\":", StringComparison.Ordinal));
    Reject<InvalidDataException>(() => f.Read(), "duplicate persisted JSON field is rejected before mutation");
    f.RawAccount(json.Replace("\"Quantity\":1,", "", StringComparison.Ordinal));
    Reject<InvalidDataException>(() => f.Read(), "missing persisted financial field cannot deserialize into a default");
}

using (var f = new Fixture())
{
    f.Configure("payg"); f.Fund(10); f.Rate(10); f.Use("persian-audit");
    var charge = f.Read().Account.Entries.Single(x => x.Kind == "usage");
    var command = new BillingCreditNoteCommand(f.Read().Account.Revision, "persian-note", charge.Id, null, 1,
        new string('د', 1000), new string('م', 1000));
    f.Billing.ApplyCreditNote(f.Tenant, command, "finance");
    Check(f.Read().Account.Entries.Single(x => x.Kind == "credit-note").Reason == command.Reason
        && f.Read().Account.Entries.Single(x => x.Kind == "receipt" && x.Key == command.Key).Reference.Length > 8192,
        "maximum Persian audit fields retain their full escaped immutable receipt");
    var before = f.Snapshot(); f.Billing.ApplyCreditNote(f.Tenant, command, "finance");
    Check(f.Snapshot() == before && f.Read().WalletBalance == 1, "long Persian credit receipt replays without duplicate credit");
}

using (var f = new Fixture())
{
    f.Clock.Now = Fixture.Start.AddMonths(1);
    var account = f.Read().Account;
    var entries = Enumerable.Range(0, 10001).Select(index => new LedgerEntry(Guid.NewGuid(), "bound-" + index,
        "receipt", null, null, 0, 0, Fixture.Start, "fixture", "retained receipt")).ToArray();
    f.Database.Transaction(() => { f.Database.Put("accounting", f.Tenant.ToString(), account with { Entries = entries }); return true; });
    var before = f.Snapshot();
    Reject<InvalidOperationException>(() => f.Invoice("bounded"), "invoice counts receipts and rejects ledger scan bound plus one");
    Check(f.Snapshot() == before, "incomplete bounded scan appends no invoice or receipt");
    f.Database.Transaction(() => { f.Database.Put("accounting", f.Tenant.ToString(), account with { Entries = entries[..10000] }); return true; });
    Check(f.Invoice("bounded-exact").Account.Invoices.Single().Lines is { Length: 0 }, "invoice permits the exact ledger scan bound with complete coverage");
}

using (var f = new Fixture())
{
    f.Configure(); f.Clock.Now = Fixture.Start.AddMonths(1);
    var account = f.Read().Account;
    var entries = Enumerable.Range(0, 4000).Select(index => new LedgerEntry(Guid.NewGuid(), new string('k', 180) + index,
        "usage", new string('p', 200), new string('m', 100), 1, 1, Fixture.Start.AddDays(1), new string('a', 200), new string('r', 1000), UnitPrice: 1)).ToArray();
    var large = account with { Entries = entries };
    Check(JsonSerializer.SerializeToUtf8Bytes(large).Length < 8 * 1024 * 1024, "snapshot-growth fixture starts inside supported account read capacity");
    f.Database.Transaction(() => { f.Database.Put("accounting", f.Tenant.ToString(), large); return true; });
    var before = f.Snapshot();
    Reject<InvalidOperationException>(() => f.Invoice("capacity-guard"), "invoice snapshot growth beyond read capacity is rejected before commit");
    Check(f.Snapshot() == before && f.Read().Account.Invoices.Length == 0, "capacity rejection preserves the original readable account and outbox");
}

Check(BillingBalance.Charge(1, .005m, "USD") == .01m && BillingBalance.Charge(1, .004m, "USD") == 0, "charge rounding uses exact coefficients and away-from-zero minor units");
Check(BillingBalance.Charge(.5m, 1, "IRR") == 1 && BillingBalance.Charge(.499m, 1, "IRR") == 0, "IRR boundary rounding is exact");
Check(BillingBalance.Add(9007199254740993.01m, .09m) == 9007199254740993.10m, "sum retains large exact money above JavaScript precision");
Reject<OverflowException>(() => BillingBalance.Add(79228162514264337593543950334m, .01m), "sum rejects silently rounded cents near decimal capacity");
Reject<OverflowException>(() => BillingBalance.Sum([decimal.MaxValue, 1]), "sum rejects decimal overflow");
Reject<OverflowException>(() => BillingBalance.Charge(decimal.MaxValue, 2, "IRR"), "rounded charge cannot exceed decimal capacity");
Console.WriteLine($"PASS Billing lifecycle: {checks} checks");

sealed class Fixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fanasa-billing-lifecycle-" + Guid.NewGuid());
    public InMemoryAccessManagement Access { get; } = new();
    public FabricDatabase Database { get; }
    public Clock Clock { get; } = new(Start.AddDays(10));
    public Guid Tenant { get; }
    public AccountingStore Billing { get; }
    private int sequence;
    public Fixture()
    {
        Database = new(Path.Combine(Root, "fabric.db")); Tenant = Access.GetTenants().Single().Id;
        Access.RegisterProduct(new("billing-test", "Billing test", "billing-test", null, "fanasa.ayin", null)); Billing = new(Root, Access, Clock, Database);
    }
    public AccountView Read() => Billing.Read(Tenant);
    private BillingCommand Command(string operation, string key) => new(Read().Account.Revision, operation, key,
        null, null, 0, "billing-test", "item", 0, 1, 0, "approval", null, null);
    public AccountView Configure(string mode = "postpaid", string currency = "IRR", decimal credit = 1000000)
        => Billing.Execute(Tenant, Command("configure", "config-" + ++sequence) with { Mode = mode, Currency = currency, CreditLimit = credit }, "finance");
    public AccountView Rate(decimal amount) => Billing.Execute(Tenant, Command("tariff", "rate-" + ++sequence) with { UnitPrice = amount }, "finance");
    public AccountView Fund(decimal amount) => Billing.Execute(Tenant, Command("credit", "fund-" + ++sequence) with { Amount = amount }, "finance");
    public AccountView Use(string key, decimal quantity = 1) => Billing.Execute(Tenant, Command("usage", key) with { Quantity = quantity }, "business");
    public AccountView Pay(string key, Guid invoice, decimal amount) => Billing.Execute(Tenant, Command("payment", key) with { InvoiceId = invoice, Amount = amount }, "payer");
    public AccountView Invoice(string key) => Billing.IssueInvoice(Tenant, new(Read().Account.Revision, key, "2026-09", "approved closure"), "finance");
    public AccountView Credit(string key, Guid charge, decimal amount, Guid? invoice = null) => Billing.ApplyCreditNote(Tenant,
        new(Read().Account.Revision, key, charge, invoice, amount, "approved correction", "case/reference"), "finance");
    public string Snapshot() => JsonSerializer.Serialize(new { Account = Database.ReadDocument("accounting", Tenant.ToString())?.Json, Outbox = Database.Outbox() });
    public void RawAccount(string json)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "fabric.db"), Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE fabric_documents SET json=$json WHERE kind='accounting' AND key=$key";
        command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$key", Tenant.ToString()); command.ExecuteNonQuery();
    }
    public UsageEventV1 Event(string operation) => new("1.0", Guid.NewGuid(), Start.AddDays(2), Start.AddDays(2).AddSeconds(1),
        new("organization-backend", "product-backend", "billing-test"), "development", Tenant, Tenant, "billing-test", "contract", 1,
        "allocation", "2026-09", new(operation, "organization.unit.save/v1", "business", Guid.NewGuid(), UsageEventV1.Hash(operation), Start.AddDays(2).AddSeconds(-1), "organization-unit-committed/v1"),
        new("decision", "policy/v1", "admission"), new("organization.change", "1", "change", "1"),
        new("frozen-price", 1, Start, Start.AddMonths(1), "12", "IRR", 0, "away-from-zero", "postpaid"), "completed",
        new("receipt-" + operation, UsageEventV1.Hash("result:" + operation), "fabric://result/" + operation), Guid.NewGuid());
    public void Dispose() => Database.Dispose();
}
sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
sealed class Validator : IUsageSnapshotValidator { public void ValidateNew(UsageEventV1 usage) => usage.Validate(); }
