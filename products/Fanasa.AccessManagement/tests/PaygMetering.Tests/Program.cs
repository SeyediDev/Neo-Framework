using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;
using Microsoft.Extensions.Configuration;

var directory = Path.Combine(Path.GetTempPath(), "fanasa-payg-metering-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var count = 0;
if (args.Length != 0 && args is not ["--evidence", _]) throw new ArgumentException("Use --evidence <output.json> to retain synthetic E2E evidence.");
var evidencePath = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    count++; Console.WriteLine("PASS " + label);
}
PaygTests.Run(directory, Check, evidencePath);
Console.WriteLine($"PASS {count} PAYG metering assertions");

static class PaygTests
{
    public static void Run(string directory, Action<bool, string> check, string? evidencePath)
    {
        ExactBalanceAndWire(directory, check);
        Rollback(directory, check);
        Replay(directory, check);
        CurrentAccess(directory, check);
        Concurrency(directory, check);
        AccountMismatch(directory, check);
        ProjectedPayg(directory, check);
        EndToEndHistoricalInvoice(directory, check, evidencePath);
    }

    private static void ExactBalanceAndWire(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "exact", 12);
        var request = f.Command("exact"); var result = f.Execute(request);
        var usage = f.Usage().Single(); var account = f.Billing.Read(f.Tenant);
        var charge = account.Account.Entries.Single(x => x.Kind == "usage");
        check(result.Revision == 1 && result.Units.Single().Code == "exact" && account.WalletBalance == 0
            && account.Outstanding == 0 && usage.RateSnapshot.BillingMode == "payg" && usage.Amount == 12
            && charge.Amount == 12 && charge.UnitPrice == 12 && charge.Quantity == 1
            && charge.Id == UsageEventV1.StableGuid(usage.BusinessKey + ":charge"),
            "real unit.save commits one PAYG charge and consumes the exact wallet balance");
        check(f.Database.Read<OrgState>("organization.history", $"{f.Tenant}:1") is not null
            && f.Database.Read<OrgReceipt>("organization.receipt", $"{f.Tenant}:{request.RequestId}")?.Revision == 1
            && f.Database.All<MeasuredEventReceipt>("measured.event").Length == 1
            && f.Database.Outbox().Count(x => x.Kind == "UsageCommitted") == 1
            && f.Database.Outbox().Count(x => x.Kind == "OrganizationChanged") == 1,
            "PAYG domain history receipt usage journal and outbox commit in shared Fabric");
        var parsed = UsageEventV1.FromJson(usage.ToWireJson());
        check(parsed.RateSnapshot == usage.RateSnapshot && parsed.BusinessKey == usage.BusinessKey && parsed.Amount == 12,
            "UsageEvent 1.0 strict wire contract accepts the frozen PAYG snapshot");
        (parsed with { RateSnapshot = parsed.RateSnapshot with { BillingMode = "postpaid" } }).Validate();
        // Exercise the amount property independently; the pilot wire contract still requires quantity one.
        var precisionBoundary = parsed with
        {
            Meter = parsed.Meter with { Quantity = "100000000000995001.000001" },
            RateSnapshot = parsed.RateSnapshot with { UnitPrice = "0.999999", Currency = "USD", MinorUnitScale = 2 }
        };
        check(precisionBoundary.Amount == 99999900000995000m,
            "usage amount rounds the exact product once without decimal multiplication moving a half-cent boundary");
        Reject<ArgumentException>(precisionBoundary.Validate, check, "exact arithmetic does not widen the pilot's quantity-one contract");
        Reject<ArgumentException>(() => (parsed with { RateSnapshot = parsed.RateSnapshot with { BillingMode = "subscription" } }).Validate(), check,
            "PAYG support preserves rejection of unsupported billing modes");
        var before = f.Snapshot();
        check(f.Execute(request).Revision == result.Revision && f.Snapshot() == before,
            "an exact PAYG request retries with a zero wallet and no extra debit");
        Reject<MeasuredAccountingException>(() => f.Execute(f.Command("insufficient")), check,
            "fresh PAYG work rejects insufficient wallet balance");
        check(f.Snapshot() == before && f.Database.All<MeasuredUsageOperation>("measured.operation").All(x => x.Status == "committed"),
            "unfunded local PAYG leaves no domain receipt pending evidence ledger or outbox");
        using var free = new PaygFixture(directory, "free", 0, price: "0"); free.Execute(free.Command("free"));
        check(free.Billing.Read(free.Tenant).WalletBalance == 0 && free.Usage().Single().Amount == 0
            && free.Billing.Read(free.Tenant).Account.Entries.Count(x => x.Kind == "usage") == 1,
            "zero-price PAYG needs no credit and retains committed usage evidence");
    }

    private static void Rollback(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "rollback", 24);
        var request = f.Command("rollback"); var before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Database.Transaction<bool>(() =>
        {
            f.Execute(request); throw new InvalidOperationException("injected failure after actual organization staging");
        }), check, "failure after actual PAYG domain and debit staging is injected");
        check(f.Snapshot() == before, "outer failure rolls back PAYG domain history receipts usage charge and outbox");
        f.Execute(request);
        check(f.Billing.Read(f.Tenant).WalletBalance == 12 && f.Usage().Length == 1,
            "the same request succeeds once after a rolled back PAYG attempt");
        before = f.Snapshot();
        Reject<ArgumentException>(() => f.Execute(f.Command("rollback")), check, "invalid domain mutation is rejected before PAYG commit");
        check(f.Snapshot() == before, "rejected domain mutation cannot consume PAYG credit or create evidence");
    }

    private static void Replay(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "replay", 100);
        var request = f.Command("original"); var original = f.Execute(request); var originalUsage = f.Usage().Single();
        f.Clock.Now = f.Start.AddHours(2); f.Bill("tariff", "changed-current-tariff", price: 99);
        var before = f.Snapshot();
        using (var reopened = f.Connect())
        {
            var response = reopened.Store.Execute(f.Tenant, request, PaygFixture.Actor, f.Principal);
            check(JsonSerializer.Serialize(response) == JsonSerializer.Serialize(original) && f.Snapshot() == before,
                "reopen after lost acknowledgement returns the original PAYG result without a second debit");
        }
        Reject<InvalidOperationException>(() => f.Execute(request with { Code = "changed" }), check, "changed payload cannot reuse a PAYG request identity");
        check(f.Snapshot() == before, "conflicting retry preserves the original PAYG result and charge");
        f.Execute(f.Command("new-rate"));
        check(f.Billing.Read(f.Tenant).WalletBalance == 70 && f.Usage().Select(x => x.RateSnapshot.RateVersion).Order().SequenceEqual([7, 8])
            && f.Usage().Single(x => x.RateSnapshot.RateVersion == 7).ToWireJson() == originalUsage.ToWireJson()
            && f.Billing.Read(f.Tenant).Account.Tariffs.Single().UnitPrice == 99,
            "fresh PAYG work uses v8 while retry preserves v7 independently of current tariff");
        f.Clock.Now = f.Start.AddDays(2); before = f.Snapshot();
        check(f.Execute(request).Revision == original.Revision && f.Snapshot() == before,
            "expired rate windows permit exact currently authorized PAYG receipt replay");
        Reject<UnauthorizedAccessException>(() => f.Execute(f.Command("expired-rate")), check, "expired configured rates reject fresh PAYG work");
        check(f.Snapshot() == before, "rate rejection cannot create PAYG business or financial state");
        var forged = originalUsage with { EventId = Guid.NewGuid(), Operation = originalUsage.Operation with { Id = "forged-operation", RequestId = Guid.NewGuid() } };
        Reject<UnauthorizedAccessException>(() => f.Intake.Accept(UsageEventV1.FromJson(forged.ToWireJson()), true), check,
            "deserialized new PAYG evidence cannot impersonate authenticated local invocation proof");
        check(f.Snapshot() == before, "untrusted PAYG evidence cannot enter the pending or committed journal");
    }

    private static void CurrentAccess(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "access", 24);
        var request = f.Command("authorized"); f.Execute(request);
        void Denied(string label)
        {
            var beforeDenial = f.Snapshot();
            Reject<UnauthorizedAccessException>(() => f.Execute(request), check, label);
            check(f.Snapshot() == beforeDenial, "denied PAYG replay creates no business or financial side effects");
        }
        f.Access.SetGrant(new(f.Tenant, PaygFixture.Actor, "organization.write", f.Clock.Now.AddMinutes(-1)), "admin", "revoke");
        Denied("current durable grant revocation rejects PAYG replay");
        f.Access.SetGrant(new(f.Tenant, PaygFixture.Actor, "organization.write", null), "admin", "restore");
        f.Access.SetSubscription(f.Tenant, f.SubscriptionId, "suspended", null, "admin", "suspend");
        Denied("current subscription suspension rejects PAYG replay");
        f.Access.SetSubscription(f.Tenant, f.SubscriptionId, "active", f.Clock.Now.AddMonths(1), "admin", "restore");
        f.Access.ApplyContractEvent(new("expire-payg", f.Tenant, "test-contract", "organization-fabric", "fanasa.ayin", "usage-payg",
            "ContractExpired", null, "change", f.Clock.Now.AddSeconds(-1)));
        Denied("current entitlement expiry rejects PAYG replay");
        f.Access.ApplyContractEvent(new("renew-payg", f.Tenant, "test-contract", "organization-fabric", "fanasa.ayin", "usage-payg", "ContractRenewed", null, "change", f.Clock.Now));
        var before = f.Snapshot();
        check(f.Execute(request).Revision == 1 && f.Snapshot() == before && f.Billing.Read(f.Tenant).WalletBalance == 12 && f.Usage().Length == 1,
            "restored access returns historical PAYG response without a new debit");
    }

    private static void Concurrency(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "wallet-race", 12); using var other = f.Connect();
        var results = new ConcurrentBag<bool>();
        Parallel.For(0, 2, i =>
        {
            var database = i == 0 ? f.Database : other.Database; var store = i == 0 ? f.Store : other.Store;
            try
            {
                database.Transaction(() => store.Execute(f.Tenant, PaygFixture.Command(store.Read(f.Tenant).Revision, "wallet-race-" + i), PaygFixture.Actor, f.Principal));
                results.Add(true);
            }
            catch (MeasuredAccountingException) { results.Add(false); }
        });
        check(results.Count(x => x) == 1 && results.Count(x => !x) == 1 && f.Billing.Read(f.Tenant).WalletBalance == 0
            && f.Store.Read(f.Tenant).Revision == 1 && f.Store.Read(f.Tenant).Units.Length == 1 && f.Usage().Length == 1
            && f.Database.All<OrgState>("organization.history").Length == 1 && f.Database.All<OrgReceipt>("organization.receipt").Length == 1,
            "two Fabric connections with fresh revisions admit one distinct operation against one PAYG balance");
        using var same = new PaygFixture(directory, "same-request-race", 24); using var sameOther = same.Connect();
        var request = same.Command("same-request"); var responses = new ConcurrentBag<OrgState>();
        Parallel.For(0, 2, i => responses.Add((i == 0 ? same.Store : sameOther.Store).Execute(same.Tenant, request, PaygFixture.Actor, same.Principal)));
        check(responses.Count == 2 && responses.All(x => x.Revision == 1) && same.Billing.Read(same.Tenant).WalletBalance == 12
            && same.Usage().Length == 1 && same.Store.Read(same.Tenant).Units.Length == 1 && same.Database.Outbox().Count(x => x.Kind == "UsageCommitted") == 1,
            "concurrent delivery of one PAYG request creates one domain result debit and usage outbox");
    }

    private static void AccountMismatch(string directory, Action<bool, string> check)
    {
        foreach (var mismatch in new[] { "mode", "currency" })
        {
            using var f = new PaygFixture(directory, "mismatch-" + mismatch, 0); var account = f.Billing.Read(f.Tenant).Account;
            f.Database.Transaction(() =>
            {
                f.Database.Put("accounting", f.Tenant.ToString(), mismatch == "mode" ? account with { Mode = "postpaid", CreditLimit = 100 } : account with { Currency = "USD" });
                return true;
            });
            var before = f.Snapshot();
            Reject<MeasuredAccountingException>(() => f.Execute(f.Command("mismatch")), check, "PAYG rejects account " + mismatch + " that differs from its frozen rate");
            check(f.Snapshot() == before, "account " + mismatch + " mismatch rolls back the entire PAYG operation");
        }
    }

    private static void ProjectedPayg(string directory, Action<bool, string> check)
    {
        using var f = new PaygFixture(directory, "projected", 12);
        var contracts = new ContractPolicyStore(f.Database, f.Access, f.Registry, ContractPolicyModule.FromOutputDirectory(), f.Clock);
        var prices = new OrganizationUsagePriceStore(f.Database, f.Access, f.Registry, f.Clock);
        var values = new Dictionary<string, string?>
        {
            ["UsageMetering:Organization:Enabled"] = "true", ["UsageMetering:Organization:BindingSource"] = "contract-policy",
            ["UsageMetering:Organization:Environment"] = "development", ["UsageMetering:Organization:ProducerId"] = "organization-backend",
            ["UsageMetering:Organization:DeploymentId"] = "test-host", ["UsageMetering:Organization:ContractLinks:0:ConsumerTenantId"] = f.Tenant.ToString("D"),
            ["UsageMetering:Organization:ContractLinks:0:ContractId"] = "commercial-contract", ["UsageMetering:Organization:ContractLinks:0:SubscriptionId"] = "commercial-subscription",
            ["UsageMetering:Organization:ContractLinks:0:OfferId"] = "commercial-offer", ["UsageMetering:Organization:ContractLinks:0:CentralSubscriptionId"] = f.SubscriptionId.ToString("D"),
            ["UsageMetering:Organization:ContractLinks:0:CentralPricingPlanId"] = f.PlanId.ToString("D")
        };
        var scope = new AllocationScope(f.Tenant.ToString("D"), ["organization-fabric"], ["development"], ["organization"]);
        var projection = new ContractAccessProjection
        {
            SchemaVersion = "2.0.0", ProfileId = "fanasa.contract-policy/2", ContractId = "commercial-contract", ContractVersion = 7,
            OwnerTenantId = f.Owner.ToString("D"), ConsumerTenantId = f.Tenant.ToString("D"), ProductId = "organization-fabric",
            OfferId = "commercial-offer", SubscriptionId = "commercial-subscription", Status = "Active",
            ValidFrom = ProjectionUtc(f.Start.AddDays(-1)), ValidTo = ProjectionUtc(f.Start.AddDays(1)), EffectiveAt = ProjectionUtc(f.Start.AddMinutes(-1)),
            FeatureEntitlements = ["organization.unit.save"], EligibleRoles = ["ApiInvoker"], Seats = new(20, "active-membership", "consumer-product-subscription"),
            Limits = new()
            {
                AllocationId = "commercial-allocation", ConfigRevision = 1, Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope),
                Meter = "api.request.admitted", Unit = "request", Rate = new("token-bucket", 5, 10), Quota = new("fixed-calendar-utc", "month", 1000)
            },
            ZoneConstraints = ["ir-private-1"], PricingReference = new("commercial-price", 1), Policy = new("organization-unit-save", 3, "organization-unit-save/1", "deny-overrides", [])
        };
        var binding = new ContractProducerBinding("https://identity.invalid/realms/fanasa", "contracts-api", "contracts-client", "contracts.project");
        var producer = new ClaimsPrincipal(new ClaimsIdentity([new("iss", binding.Issuer), new("aud", binding.Audience), new("azp", binding.ClientId), new("scope", binding.RequiredScope)], "test-producer"));
        var envelope = new ContractProjectionEvent
        {
            SchemaVersion = "2.0.0", EventId = "payg-projection-seven", Producer = ContractEventAdapter.AuthorizedProducer, AggregateVersion = 7,
            OccurredAt = ProjectionUtc(f.Start), CorrelationId = "g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Projection = projection
        };
        var applied = contracts.Apply(JsonSerializer.Serialize(envelope, ContractJson.Options), producer, binding, () => true);
        check(applied.Status == "applied", "PAYG uses an authenticated actual central contract projection");
        var rate = new UsageRateSnapshot("commercial-price", 1, f.Start.AddDays(-1), f.Start.AddHours(1), "12", "IRR", 0, "away-from-zero", "payg");
        prices.Publish(rate.RateId, 1, rate, "publisher", "frozen PAYG price one");
        prices.Publish(rate.RateId, 2, rate with { RateVersion = 2, UnitPrice = "18" }, "publisher", "newer PAYG price two");
        var resolver = new OrganizationUsageMetering(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), f.Access, f.Registry, f.Clock, contracts, prices);
        var store = new OrganizationStore(f.Root, f.Access, database: f.Database, metering: resolver, measuredUsage: new MeasuredUsageStore(f.Database, f.Billing, resolver, f.Clock));
        store.Execute(f.Tenant, f.Command("projected-payg"), PaygFixture.Actor, f.Principal);
        check(f.Billing.Read(f.Tenant).WalletBalance == 0 && f.Usage().Single().RateSnapshot == rate && f.Usage().Single().AllocationId == projection.Limits.AllocationId,
            "central PAYG plan and projection select the exact persisted price version rather than latest");
    }

    private static void EndToEndHistoricalInvoice(string directory, Action<bool, string> check, string? evidencePath)
    {
        using var f = new PaygFixture(directory, "unit-save-to-invoice", 100);
        f.Access.SetGrant(new(f.Tenant, PaygFixture.Actor, "billing.read", null), "admin", "synthetic E2E reader");
        bool Allows() => f.Access.GetTenants().Any(x => x.Id == f.Tenant && x.IsActive)
            && f.Access.GetUsers(f.Tenant).Any(x => x.IsActive && x.KeycloakSubject == PaygFixture.Actor)
            && f.Access.GetGrants(PaygFixture.Actor).Any(x => x.TenantId == f.Tenant && x.Permission == "billing.read"
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > f.Clock.Now));
        var result = f.Execute(f.Command("invoice-source"));
        var usage = f.Usage().Single();
        var period = BillingBalance.Period(usage.OccurredAt); var (from, to) = BillingBalance.PeriodBounds(period);
        var report = new ConsumerUsageReportStore(f.Database, f.Clock).Read(f.Tenant, from, to, Allows);
        check(result.Revision == 1 && report.Status == "ready" && report.Currency == "IRR" && report.TotalAmount == "12"
            && report.Items.Single() is { Quantity: "1", Amount: "12", ChargeCount: 1, MeterVersion: "1", Unit: "change" },
            "E2E actual unit.save becomes one exact PAYG consumer usage report item");
        f.Clock.Now = to.AddMinutes(1);
        f.Bill("tariff", "e2e-new-current-price", price: 99);
        var issued = f.Billing.IssueInvoice(f.Tenant, new(f.Billing.Read(f.Tenant).Account.Revision,
            "e2e-closed-month-invoice", period, "synthetic actual-unit-save E2E"), "finance-test");
        var invoice = issued.Account.Invoices.Single(); var line = invoice.Lines!.Single();
        check(invoice.Period == period && invoice.Currency == "IRR" && invoice.Amount == usage.Amount && invoice.From == from && invoice.To == to
            && line.LedgerKey == usage.BusinessKey && line.UnitPrice == usage.UnitPrice && line.Quantity == usage.Quantity
            && line.GrossAmount == 12 && line.CreditAmount == 0 && line.NetAmount == 12 && line.OccurredAt == usage.OccurredAt
            && !string.IsNullOrWhiteSpace(invoice.ContentHash) && issued.Account.Tariffs.Single().UnitPrice == 99,
            "E2E closed-month invoice freezes the actual historical PAYG usage rate and exact ledger amount");
        var frozenInvoice = JsonSerializer.Serialize(invoice);
        f.Bill("tariff", "e2e-price-after-invoice", price: 111);
        var body = ConsumerAccountProjection.Read(f.Database, f.Billing, f.Tenant, f.Clock, Allows);
        var summary = body.Invoices.Single();
        check(body is { Status: "ready", Mode: "payg", Currency: "IRR", WalletBalance: "88", Outstanding: "0", AvailableCredit: "0" }
            && summary.Id == invoice.Id && summary.Period == period && summary.Amount == "12" && summary.CreditedAmount == "0"
            && summary.PaidAmount == "0" && summary.RemainingAmount == "12" && summary.Status == "statement"
            && JsonSerializer.Serialize(f.Billing.Read(f.Tenant).Account.Invoices.Single()) == frozenInvoice,
            "E2E consumer account returns exact PAYG balances and the immutable historical invoice summary");
        if (evidencePath is not null)
        {
            string Exact(decimal value) => value.ToString(CultureInfo.InvariantCulture);
            var evidence = new
            {
                synthetic = true, scenario = "actual-unit-save-to-consumer-invoice", producedAt = DateTimeOffset.UtcNow,
                consumerTenantId = f.Tenant, period, operationKey = usage.BusinessKey,
                price = new { id = usage.RateSnapshot.RateId, version = usage.RateSnapshot.RateVersion,
                    unitPrice = usage.RateSnapshot.UnitPrice, currency = usage.RateSnapshot.Currency },
                exactAmountString = Exact(usage.Amount), ledgerChargeCount = f.Billing.Read(f.Tenant).Account.Entries.Count(x => x.Kind == "usage"),
                reportTotal = report.TotalAmount,
                invoice = new { id = invoice.Id, hash = invoice.ContentHash, exactAmountString = Exact(invoice.Amount),
                    lineSummary = invoice.Lines!.Select(x => new { entryId = x.EntryId, operationKey = x.LedgerKey,
                        product = x.ProductKey, meter = x.Metric, quantity = Exact(x.Quantity),
                        unitPrice = x.UnitPrice.HasValue ? Exact(x.UnitPrice.Value) : null,
                        grossAmount = Exact(x.GrossAmount), creditedAmount = Exact(x.CreditAmount), netAmount = Exact(x.NetAmount), x.OccurredAt }).ToArray() },
                accountSummary = body
            };
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            check(File.Exists(evidencePath), "E2E writes synthetic acceptance evidence from its actual executed usage invoice and account projections");
        }
    }

    private static string ProjectionUtc(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
}

sealed class PaygFixture : IDisposable
{
    public const string Actor = "payg-member";
    public string Root { get; }
    public string FabricPath { get; }
    public PlatformRegistry Registry { get; }
    public FabricDatabase Database { get; }
    public PersistentAccessManagement Access { get; }
    public Guid Owner { get; }
    public Guid Tenant { get; }
    public Guid PlanId { get; }
    public Guid SubscriptionId { get; }
    public PaygClock Clock { get; }
    public DateTimeOffset Start { get; }
    public IConfiguration Settings { get; }
    public AccountingStore Billing { get; }
    public MeasuredUsageStore Intake { get; }
    public OrganizationStore Store { get; }
    public ClaimsPrincipal Principal { get; }

    public PaygFixture(string directory, string name, decimal credit, string price = "12")
    {
        Root = Path.Combine(directory, name); Directory.CreateDirectory(Root); FabricPath = Path.Combine(Root, "fabric.db");
        Registry = new PlatformRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = Path.Combine(Root, "registry.db") }).Build());
        Database = new FabricDatabase(FabricPath); Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(Registry));
        Owner = Access.GetTenants().Single().Id; Tenant = Access.CreateTenant("payg-" + name, "PAYG " + name).Id;
        Registry.SetTenantSource(Access.GetTenants); Registry.SetMembershipSource(Access.GetUsers);
        Registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", ContractPolicyStore.ProductAudience, "fanasa.ayin", null, null, Owner, "fanasa"), "test");
        Access.AddUser(new(Tenant, Actor, "PAYG Member")); Access.SetGrant(new(Tenant, Actor, "organization.write", null), "admin", "PAYG fixture");
        var plan = Access.RegisterPlan(new("organization-fabric", "usage-payg", "Usage PAYG", "payg", "IRR", 0, 0)); PlanId = plan.Id;
        SubscriptionId = Access.Subscribe(new(Tenant, "organization-fabric", plan.Id, 10)).Id;
        Clock = new PaygClock(DateTimeOffset.UtcNow.AddMinutes(1)); Start = Clock.Now;
        Access.ApplyContractEvent(new("activate-payg-" + name, Tenant, "test-contract", "organization-fabric", "fanasa.ayin", "usage-payg", "ContractActivated", null, "change", Start.AddMinutes(-2)));
        Billing = new AccountingStore(Root, Access, Clock, Database); if (credit > 0) Bill("credit", "fund-wallet", credit);
        Settings = CreateSettings(price); var metering = new OrganizationUsageMetering(Settings, Access, Registry, Clock);
        Intake = new MeasuredUsageStore(Database, Billing, metering, Clock); Store = new OrganizationStore(Root, Access, database: Database, metering: metering, measuredUsage: Intake);
        Principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", Actor), new("tenant_permission", Tenant + ":organization.write")], "test"));
    }
    public OrgCommand Command(string code) => Command(Store.Read(Tenant).Revision, code);
    public static OrgCommand Command(long revision, string code) => new(revision, "unit.save", null, code, code, null, "team", null, 1, null, null, null, null, "PAYG fixture", RequestId: Guid.NewGuid().ToString("D"));
    public OrgState Execute(OrgCommand command) => Store.Execute(Tenant, command, Actor, Principal);
    public UsageEventV1[] Usage() => Database.All<MeasuredUsageOperation>("measured.operation").Select(x => UsageEventV1.FromJson(x.EventJson)).ToArray();
    public AccountView Bill(string operation, string key, decimal amount = 0, decimal price = 0) => Billing.Execute(Tenant,
        new(Billing.Read(Tenant).Account.Revision, operation, key, "payg", "IRR", 0, "organization-fabric", "organization.change", price, 1, amount, "fixture", null, null), "admin");
    public string Snapshot() => JsonSerializer.Serialize(new
    {
        Organization = Store.Read(Tenant), Account = Billing.Read(Tenant).Account,
        History = Database.All<OrgState>("organization.history"), Receipts = Database.All<OrgReceipt>("organization.receipt"),
        Usage = Database.All<MeasuredUsageOperation>("measured.operation"), Events = Database.All<MeasuredEventReceipt>("measured.event"), Outbox = Database.Outbox()
    });
    public PaygConnection Connect() => new(this);
    private IConfiguration CreateSettings(string price)
    {
        var values = new Dictionary<string, string?>
        {
            ["UsageMetering:Organization:Enabled"] = "true", ["UsageMetering:Organization:Environment"] = "development",
            ["UsageMetering:Organization:ProducerId"] = "organization-backend", ["UsageMetering:Organization:DeploymentId"] = "test-host"
        };
        void Binding(int index, int version, string unitPrice, DateTimeOffset from, DateTimeOffset to)
        {
            var prefix = $"UsageMetering:Organization:Bindings:{index}:";
            values[prefix + "ConsumerTenantId"] = Tenant.ToString("D"); values[prefix + "ContractId"] = "test-contract";
            values[prefix + "ContractVersion"] = "7"; values[prefix + "AllocationId"] = "test-allocation";
            values[prefix + "AllocationPeriodId"] = Start.ToString("yyyy-MM", CultureInfo.InvariantCulture); values[prefix + "PlanKey"] = "usage-payg";
            values[prefix + "PricingReference"] = "organization-change-rate";
            var rate = prefix + "RateSnapshot:";
            values[rate + "RateId"] = "organization-change-rate"; values[rate + "RateVersion"] = version.ToString(CultureInfo.InvariantCulture);
            values[rate + "EffectiveFrom"] = Utc(from); values[rate + "EffectiveTo"] = Utc(to);
            values[rate + "UnitPrice"] = unitPrice; values[rate + "Currency"] = "IRR"; values[rate + "MinorUnitScale"] = "0";
            values[rate + "Rounding"] = "away-from-zero"; values[rate + "BillingMode"] = "payg";
        }
        Binding(0, 7, price, Start.AddDays(-1), Start.AddHours(1)); Binding(1, 8, "18", Start.AddHours(1), Start.AddDays(1));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static string Utc(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    public void Dispose() => Database.Dispose();
}

sealed class PaygConnection : IDisposable
{
    public FabricDatabase Database { get; }
    public OrganizationStore Store { get; }
    public PaygConnection(PaygFixture fixture)
    {
        Database = new FabricDatabase(fixture.FabricPath);
        var access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(fixture.Registry));
        var billing = new AccountingStore(fixture.Root, access, fixture.Clock, Database);
        var metering = new OrganizationUsageMetering(fixture.Settings, access, fixture.Registry, fixture.Clock);
        Store = new OrganizationStore(fixture.Root, access, database: Database, metering: metering, measuredUsage: new MeasuredUsageStore(Database, billing, metering, fixture.Clock));
    }
    public void Dispose() => Database.Dispose();
}

sealed class PaygClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
