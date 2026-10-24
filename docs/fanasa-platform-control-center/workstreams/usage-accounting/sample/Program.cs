using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using UsageAccounting.Sample;

if (args.Length > 0 && args[0] == "--integration")
{
    IntegrationCompatibility.Run(args[1..]);
    return;
}

var checks = new List<string>();
void Check(bool condition, string label)
{ if (!condition) throw new InvalidOperationException("FAIL " + label); checks.Add("PASS " + label); Console.WriteLine("PASS " + label); }
void Reject<T>(Action action, string label) where T : Exception
{ try { action(); } catch (T) { Check(true, label); return; } throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + label); }

var sampleRoot = FindSampleRoot();
var fixturePath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(sampleRoot, "../fixtures/usage-event-v1.json"));
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(sampleRoot, "results", "run-" + Guid.NewGuid().ToString("N"));
if (!IsWithin(output, sampleRoot) && !IsWithin(output, Path.GetTempPath())) throw new ArgumentException("Output must stay in sample/ or the temporary directory.");
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a fresh fixture output directory; the sample never overwrites an existing run.");
Directory.CreateDirectory(output);
var baseline = FixtureEvent.Load(fixturePath);
var duplicatePath = Path.Combine(output, "duplicate-json-name.json");
File.WriteAllText(duplicatePath, baseline.Payload.GetRawText().Replace("\"eventId\":", "\"eventId\": \"00000000-0000-4000-8000-000000000099\", \"eventId\":", StringComparison.Ordinal));
Reject<InvalidDataException>(() => FixtureEvent.Load(duplicatePath), "duplicate JSON object names are rejected before canonical hashing");
using var reconciliation = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixturePath)!, "reconciliation-v1.json")));
var root = reconciliation.RootElement;
var events = root.GetProperty("events").EnumerateArray().Select(x => new FixtureEvent(x.Clone())).ToArray();
Check(events.Length == 2 && baseline.EventFingerprint == events[0].EventFingerprint, "base fixture and two-event reconciliation agree");
var expected = root.GetProperty("expected");
decimal Money(JsonElement element, string property) => decimal.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);
var credit = Money(root.GetProperty("account"), "creditLimit");
var resolver = new PinnedHistoricalResolver(events);
ProposedUsageIntake Intake(FixtureContext context) => new(context.Database, context.Accounting, resolver, new FixtureClock());
var event1 = events[0]; var event2 = events[1]; var livePrice = event2.UnitPrice;

// Main ledger follows the authoritative synthetic delivery order, including out-of-order rate versions.
var mainPath = Path.Combine(output, "main");
using (var main = new FixtureContext(mainPath, event1, credit, livePrice, initialize: true))
{
    var intake = Intake(main);
    var charged = intake.Accept(event2);
    Check(charged.Disposition == "charged" && charged.Amount == 18, "new operation v8 charges 18 IRR");
    var counts = Snapshot(main, event1.Consumer);
    Check(intake.Accept(event2).Disposition == "event-replay" && Snapshot(main, event1.Consumer) == counts,
        "same event and payload replay changes no receipt, usage, ledger or outbox");
    using (var reordered = JsonDocument.Parse(JsonSerializer.Serialize(event2.Payload.EnumerateObject().Reverse().ToDictionary(x => x.Name, x => x.Value))))
        Check(intake.Accept(new FixtureEvent(reordered.RootElement.Clone())).Disposition == "event-replay", "sorted event fingerprint ignores JSON object property order");
    Reject<PayloadConflict>(() => intake.Accept(event2.Change(x => x["meter"]!["quantity"] = "2")), "same event with different payload conflicts");
    Check(Snapshot(main, event1.Consumer) == counts, "event conflict leaves all committed counts unchanged");
    var alias = event2.Change(x =>
    {
        x["eventId"] = "00000000-0000-4000-8000-000000000102";
        x["producedAt"] = "2026-10-10T09:00:00Z";
        x["correlationId"] = "44444444-4444-4444-8444-444444444499";
        x["traceId"] = "ffffffffffffffffffffffffffffffff";
        x["operation"]!["requestId"] = "33333333-3333-4333-8333-333333333399";
    });
    Check(intake.Accept(alias).Disposition == "operation-replay" && main.Database.All<RecordedUsage>(ProposedUsageIntake.Usage).Length == 1
        && main.Accounting.Read(event1.Consumer).Outstanding == 18 && main.Database.Outbox().Count(x => x.Kind == ProposedUsageIntake.ChargedKind) == 1,
        "new eventId for the same operation adds only a receipt, without another charge or outbox event");
    var afterAlias = Snapshot(main, event1.Consumer);
    foreach (var change in new Action<JsonObject>[]
    {
        x => x["operation"]!["actorId"] = "different-actor",
        x => x["operation"]!["requestFingerprint"] = new string('f', 64),
        x => x["operation"]!["acceptedAt"] = "2026-10-10T07:29:00Z",
        x => x["rateSnapshot"]!["unitPrice"] = "99",
        x => x["contractVersion"] = 9
    })
    {
        var conflicting = event2.Change(x => { x["eventId"] = Guid.NewGuid().ToString(); change(x); });
        Reject<PayloadConflict>(() => intake.Accept(conflicting), "same operation conflicts when an immutable binding changes");
    }
    Check(Snapshot(main, event1.Consumer) == afterAlias, "operation conflicts cannot create a second charge");
    Reject<InjectedCrash>(() => intake.Accept(event1, Fault.BeforeCommit), "fault before commit injected after staging receipt, usage, ledger and outbox");
    Check(Snapshot(main, event1.Consumer) == afterAlias && main.Database.Read<IntakeReceipt>(ProposedUsageIntake.Receipts, event1.EventKey) is null,
        "Fabric rollback removes every staged document and ledger/outbox change");
    Reject<IntegrationPending>(() => intake.RejectUnsafeLegacyHistoricalPath(event1), "historical v7 event blocks the unsafe current-tariff integration path");
    Check(main.Accounting.Read(event1.Consumer).Account.Tariffs.Single().UnitPrice == 18, "historical validation never mutates the live tariff");
    var malformedTime = event1.NewOperation("malformed-timeline", Guid.NewGuid().ToString()).Change(x => x["producedAt"] = "2026-10-10T06:29:00Z");
    Reject<InvalidDataException>(() => intake.Accept(malformedTime), "new work requires acceptedAt <= occurredAt <= producedAt");
    var nonUtcTime = event1.NewOperation("non-utc-time", Guid.NewGuid().ToString()).Change(x => x["occurredAt"] = "2026-10-10T10:01:00+03:30");
    Reject<InvalidDataException>(() => intake.Accept(nonUtcTime), "new work requires strict UTC Z timestamps");
    Check(Snapshot(main, event1.Consumer) == afterAlias, "malformed timeline and non-UTC events leave no accounting effects");
}

// Actual administrative recovery captures both existing Fabric and central registry SQLite files.
SqliteConnection.ClearAllPools();
var backup = Path.Combine(output, "backup");
FabricRecovery.Backup(Path.Combine(mainPath, "fabric.db"), Path.Combine(mainPath, "platform-registry.db"), backup, offlineConfirmed: true);
Check(FabricRecovery.Verify(backup).Files.Length == 2, "actual FabricRecovery verifies the offline checksummed backup bundle");
using (var main = new FixtureContext(mainPath, event1, credit, livePrice))
{
    var intake = Intake(main);
    Reject<InjectedCrash>(() => intake.Accept(event1, Fault.AfterCommit), "fault after commit loses the acknowledgement");
}
using (var main = new FixtureContext(mainPath, event1, credit, livePrice))
{
    var intake = Intake(main);
    Check(intake.Accept(event1).Disposition == "event-replay", "restart after committed-but-unacknowledged intake safely replays");
    foreach (var id in root.GetProperty("deliveryOrder").EnumerateArray().Select(x => x.GetString()!))
        intake.Accept(events.Single(x => x.EventId == id));
    VerifyLedger(main, events, expected, Check);
    var sinkPath = Path.Combine(output, "sink-receipts.json"); var sink = new IdempotentSink(sinkPath);
    Reject<InjectedCrash>(() => ProposedOutboxRelay.Deliver(main.Database, sink, crashAfterRemoteAck: true), "outbox crash after remote acknowledgement and before local checkpoint");
    var restartedSink = new IdempotentSink(sinkPath);
    ProposedOutboxRelay.Deliver(main.Database, restartedSink);
    var delivered = restartedSink.Receipts;
    Check(sink.Attempts + restartedSink.Attempts == 3 && delivered.Length == 2 && delivered.Select(x => x.BusinessKey).Distinct().Count() == 2,
        "durable sink absorbs the duplicate delivery: three attempts, two unique charge receipts");
    foreach (var receipt in delivered)
    {
        using var payload = JsonDocument.Parse(receipt.Payload);
        Check(payload.RootElement.GetProperty("TenantId").GetGuid() == event1.Consumer
            && payload.RootElement.GetProperty("OwnerTenantId").GetGuid() == event1.Owner,
            "outbox preserves consumer and owner bindings");
    }
    File.WriteAllText(Path.Combine(output, "ledger.json"), JsonSerializer.Serialize(new
    {
        Scope = "PROPOSED standalone fixture using the existing BillingAccount ledger",
        Charges = main.Accounting.Read(event1.Consumer).Account.Entries.Where(x => x.Kind == "usage").OrderBy(x => x.At).ToArray(),
        main.Accounting.Read(event1.Consumer).Outstanding,
        main.Accounting.Read(event1.Consumer).AvailableCredit,
        OwnerRevenueShare = (decimal?)null, PlatformRevenueShare = (decimal?)null, SettlementStatus = "not-defined"
    }, new JsonSerializerOptions { WriteIndented = true }));
}

SqliteConnection.ClearAllPools();
var restored = Path.Combine(output, "restored");
FabricRecovery.Restore(backup, restored, offlineConfirmed: true);
using (var recovered = new FixtureContext(restored, event1, credit, livePrice))
{
    Check(recovered.Accounting.Read(event1.Consumer).Outstanding == 18
        && recovered.Database.Read<IntakeReceipt>(ProposedUsageIntake.Receipts, event1.EventKey) is null,
        "restore has the recorded recovery point: one retained charge, one later 12 IRR charge absent");
    var recoveredIntake = Intake(recovered);
    foreach (var id in root.GetProperty("deliveryOrder").EnumerateArray().Select(x => x.GetString()!))
        recoveredIntake.Accept(events.Single(x => x.EventId == id));
    VerifyLedger(recovered, events, expected, Check);
    Check(recovered.Database.Outbox().Count(x => x.Kind == ProposedUsageIntake.ChargedKind) == 2,
        "authoritative event replay after restore reconstructs the missing charge/outbox without duplicates");
    var oldSink = new IdempotentSink(Path.Combine(output, "sink-receipts.json"));
    ProposedOutboxRelay.Deliver(recovered.Database, oldSink);
    Check(oldSink.Receipts.Length == 2 && oldSink.Attempts == 2,
        "stable business-derived charge identity lets the newer sink absorb both restored outbox deliveries");
}

// Regression proof uses an isolated real AccountingStore so the main two-charge fixture stays exact.
using (var probe = new FixtureContext(Path.Combine(output, "legacy-probe"), event1, credit, livePrice, initialize: true))
{
    probe.Accounting.Execute(event1.Consumer, probe.Command("usage", "legacy-snapshot-probe", event1)
        with { UnitPrice = 12, Amount = 12 }, "fixture");
    var legacy = probe.Accounting.Read(event1.Consumer).Account.Entries.Single(x => x.Kind == "usage");
    Check(legacy.Amount == 18 && legacy.UnitPrice == 18 && legacy.At != event1.OccurredAt,
        "existing Execute ignores requested historical price/amount and uses live tariff plus processing time");
}

var unknownPath = Path.Combine(output, "unknown-outcome");
var backend = new FixtureBackend(Path.Combine(output, "backend-business-receipt.json"));
using (var unknown = new FixtureContext(unknownPath, event1, credit, livePrice, initialize: true))
{
    Reject<TimeoutException>(() => new UnknownOutcomeGate(unknown.Database).ExecuteOnce(event1, backend), "backend commits but response times out with unknown outcome");
    Check(unknown.Accounting.Read(event1.Consumer).Outstanding == 0, "timeout does not invent completion or a charge");
}
using (var unknown = new FixtureContext(unknownPath, event1, credit, livePrice))
{
    var gate = new UnknownOutcomeGate(unknown.Database);
    Reject<InvalidOperationException>(() => gate.ExecuteOnce(event1, backend), "restart blocks blind backend replay for the same unknown business key");
    var intake = Intake(unknown);
    Check(!gate.Reconcile(event1, new FixtureBackend(Path.Combine(output, "non-authoritative-missing-status.json")), intake)
        && unknown.Accounting.Read(event1.Consumer).Outstanding == 0 && backend.Executions == 1,
        "missing non-authoritative status stays unknown and does not authorize another execution");
    Check(gate.Reconcile(event1, backend, intake) && gate.Reconcile(event1, backend, intake)
        && backend.Executions == 1 && backend.Queries == 2 && unknown.Accounting.Read(event1.Consumer).Outstanding == 12,
        "same-key backend status reconciliation accounts exactly once without reexecuting business work");
}

var unknownPendingPath = Path.Combine(output, "unknown-accounting-pending");
var pendingBackend = new FixtureBackend(Path.Combine(output, "pending-backend-business-receipt.json"));
using (var unknown = new FixtureContext(unknownPendingPath, event1, 0, livePrice, initialize: true))
{
    var gate = new UnknownOutcomeGate(unknown.Database);
    Reject<TimeoutException>(() => gate.ExecuteOnce(event1, pendingBackend), "zero-credit backend response is lost after business commit");
    Reject<IntegrationPending>(() => gate.Reconcile(event1, pendingBackend, Intake(unknown)), "unknown reconciliation retains a completed event when accounting lacks credit");
}
using (var unknown = new FixtureContext(unknownPendingPath, event1, 0, livePrice))
{
    Check(unknown.Database.Read<PendingAccounting>(ProposedUsageIntake.Pending, event1.EventKey)?.Status == "accounting-pending"
        && unknown.Accounting.Read(event1.Consumer).Outstanding == 0 && pendingBackend.Executions == 1,
        "pending evidence survives restart outside the attempt-status transaction");
    unknown.Accounting.Execute(event1.Consumer, unknown.Command("configure", "pending-backend-credit-resolution", event1) with { CreditLimit = credit }, "fixture");
    Check(new UnknownOutcomeGate(unknown.Database).Reconcile(event1, pendingBackend, Intake(unknown))
        && pendingBackend.Executions == 1 && unknown.Accounting.Read(event1.Consumer).Outstanding == 12,
        "later same-key inquiry after credit resolution charges once without backend replay");
}

using (var pending = new FixtureContext(Path.Combine(output, "capacity-pending"), event1, 0, livePrice, initialize: true))
{
    var intake = Intake(pending);
    Reject<IntegrationPending>(() => intake.Accept(event1), "completed remote event exceeding credit is accounting-pending");
    Check(pending.Database.Read<PendingAccounting>(ProposedUsageIntake.Pending, event1.EventKey)?.Status == "accounting-pending"
        && pending.Accounting.Read(event1.Consumer).Outstanding == 0 && pending.Database.All<RecordedUsage>(ProposedUsageIntake.Usage).Length == 0,
        "pending event/evidence are durable while ledger stays unchanged");
    var pendingAlias = event1.Change(x => { x["eventId"] = Guid.NewGuid().ToString(); x["operation"]!["requestId"] = Guid.NewGuid().ToString(); });
    Reject<IntegrationPending>(() => intake.Accept(pendingAlias), "equivalent new eventId also remains pending for the same zero-credit operation");
    pending.Accounting.Execute(event1.Consumer, pending.Command("configure", "fixture-credit-resolution", event1) with { CreditLimit = credit }, "fixture");
    Reject<PayloadConflict>(() => intake.Accept(event1.Change(x => x["operation"]!["actorId"] = "different-actor")),
        "credit resolution cannot charge a changed payload under the pending event key");
    var conflictingPendingOperation = event1.Change(x => { x["eventId"] = Guid.NewGuid().ToString(); x["operation"]!["actorId"] = "different-actor"; });
    Reject<PayloadConflict>(() => intake.Accept(conflictingPendingOperation), "new eventId cannot replace pending operation immutable bindings");
    Check(pending.Accounting.Read(event1.Consumer).Outstanding == 0, "both pending conflicts leave the ledger uncharged");
    Check(intake.Accept(event1).Amount == 12 && pending.Database.Read<PendingAccounting>(ProposedUsageIntake.Pending, event1.EventKey)?.Status == "resolved",
        "credit resolution replays the completed evidence into accounting without backend execution");
    Check(pending.Database.All<PendingAccounting>(ProposedUsageIntake.Pending).Length == 2
        && pending.Database.All<PendingAccounting>(ProposedUsageIntake.Pending).All(x => x.Status == "resolved")
        && intake.Accept(pendingAlias).Disposition == "operation-replay"
        && pending.Accounting.Read(event1.Consumer).Account.Entries.Count(x => x.Kind == "usage") == 1,
        "one charge resolves every pending event alias and alias replay never charges again");
}
using (var closed = new FixtureContext(Path.Combine(output, "closed-period"), event1, credit, livePrice, initialize: true))
{
    // Test setup: emulate an already-issued period; no production invoice command is installed or changed.
    var invoice = new Invoice(Guid.NewGuid(), event1.OccurredAt.ToString("yyyy-MM", CultureInfo.InvariantCulture), 77, event1.OccurredAt.AddMonths(1));
    closed.Database.Transaction(() =>
    {
        var account = closed.Accounting.Read(event1.Consumer).Account;
        closed.Database.Put("accounting", event1.Consumer.ToString(), account with { Invoices = [invoice] }); return true;
    });
    Reject<IntegrationPending>(() => Intake(closed).Accept(event1), "late event for issued period awaits manual adjustment");
    Check(closed.Accounting.Read(event1.Consumer).Account.Invoices.Single() == invoice
        && closed.Accounting.Read(event1.Consumer).Outstanding == 0
        && closed.Database.Read<PendingAccounting>(ProposedUsageIntake.Pending, event1.EventKey)?.Status == "accounting-pending",
        "closed-period event is retained and issued invoice remains unchanged");
}
using (var race = new FixtureContext(Path.Combine(output, "pending-commit-race"), event1, 0, livePrice, initialize: true))
{
    var intake = Intake(race);
    intake.BeforeRetainingPending = () =>
    {
        intake.BeforeRetainingPending = null;
        race.Accounting.Execute(event1.Consumer, race.Command("configure", "race-credit-resolution", event1) with { CreditLimit = credit }, "fixture");
        Intake(race).Accept(event1);
    };
    Check(intake.Accept(event1).Disposition == "event-replay" && race.Accounting.Read(event1.Consumer).Outstanding == 12
        && race.Database.Read<PendingAccounting>(ProposedUsageIntake.Pending, event1.EventKey) is null,
        "pending-retention race rechecks a competing committed receipt and never overwrites success");
}

checks.Add("LIMIT: snapshot append is PROPOSED; production integration must move it into AccountingStore.ExecuteMeasuredUsage.");
checks.Add("LIMIT: fixture resolver, backend status, source replay and sink are synthetic; no live endpoint, route, broker, limiter or metering setting is installed.");
checks.Add("LIMIT: backup RPO is the last offline snapshot; event/source retention and downstream reconciliation are required beyond that point.");
checks.Add("LIMIT: owner/platform revenue share, payout and settlement are undefined; only consumer usage debt is verified.");
File.WriteAllLines(Path.Combine(output, "test-output.txt"), checks);
Console.WriteLine($"{checks.Count(x => x.StartsWith("PASS ", StringComparison.Ordinal))} checks passed. Artifacts: {output}");

static string FindSampleRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "UsageAccounting.Sample.csproj"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run the fixture from its sample checkout.");
}
static bool IsWithin(string target, string parent) => target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))
    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
static string Snapshot(FixtureContext context, Guid tenant) => JsonSerializer.Serialize(new
{
    Account = context.Accounting.Read(tenant), Receipts = context.Database.All<IntakeReceipt>(ProposedUsageIntake.Receipts),
    Usage = context.Database.All<RecordedUsage>(ProposedUsageIntake.Usage), Operations = context.Database.All<ChargedOperation>(ProposedUsageIntake.Operations),
    Outbox = context.Database.Outbox()
});
static void VerifyLedger(FixtureContext context, FixtureEvent[] events, JsonElement expected, Action<bool, string> check)
{
    var view = context.Accounting.Read(events[0].Consumer);
    var ledger = view.Account.Entries.Where(x => x.Kind == "usage").OrderBy(x => x.At).ToArray();
    decimal Value(string name) => decimal.Parse(expected.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    check(ledger.Length == expected.GetProperty("usageLedgerEntries").GetInt32()
        && context.Database.All<RecordedUsage>(ProposedUsageIntake.Usage).Length == expected.GetProperty("billableOperations").GetInt32(),
        "known ledger fixture has exactly two usage and operation records");
    foreach (var message in events)
    {
        var entry = ledger.Single(x => x.Key == message.BusinessKey);
        check(entry.Amount == message.Amount && entry.UnitPrice == message.UnitPrice && entry.Quantity == message.Quantity
            && entry.At == new FixtureClock().GetUtcNow() && entry.Actor == message.Actor && entry.Reference == message.Reference
            && new FixtureEvent(context.Database.Read<RecordedUsage>(ProposedUsageIntake.Usage, message.BusinessKey)!.Event).OccurredAt == message.OccurredAt,
            "known ledger preserves historical amount/price, registration time and separate occurredAt for " + message.OperationId);
    }
    check(ledger.Sum(x => x.Amount) == Value("total") && view.Outstanding == Value("outstanding")
        && view.AvailableCredit == Value("availableCredit") && view.WalletBalance == Value("walletBalance")
        && ledger.Sum(x => x.Amount) - events.Sum(x => x.Amount) == Value("unexplainedDifference"),
        "ledger reconciles 30 IRR outstanding, 9970 credit, and zero unexplained difference");
    check(context.Accounting.Read(events[0].Owner).Outstanding == 0 && view.Account.Tariffs.Single().UnitPrice == 18,
        "owner is not charged and live v8 tariff remains 18 IRR");
}

sealed class FixtureClock : TimeProvider
{ public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-10T08:00:00Z", CultureInfo.InvariantCulture); }
sealed class FixtureContext : IDisposable
{
    public FabricDatabase Database { get; }
    public AccountingStore Accounting { get; }
    public FixtureContext(string directory, FixtureEvent baseline, decimal credit, decimal livePrice, bool initialize = false)
    {
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Platform:RegistryPath"] = Path.Combine(directory, "platform-registry.db") }).Build();
        var registry = new PlatformRegistry(configuration);
        var catalog = new InMemoryAccessManagement(registry);
        if (!catalog.GetProducts().Any(x => x.Key == baseline.Product))
            catalog.RegisterProduct(new RegisterProductRequest(baseline.Product, "Synthetic fixture product", "organization", null, "fanasa.ayin", null));
        Database = new FabricDatabase(Path.Combine(directory, "fabric.db"));
        var access = new PersistentAccessManagement(Database, catalog);
        if (initialize)
            Database.Transaction(() =>
            {
                // Fixture-only tenancy seed. Both identities stay exactly equal to the schema fixture.
                Database.Put("tenant", baseline.Owner.ToString(), new Tenant(baseline.Owner, "fixture-owner", "Fixture owner", true, baseline.AcceptedAt));
                Database.Put("tenant", baseline.Consumer.ToString(), new Tenant(baseline.Consumer, "fixture-consumer", "Fixture consumer", true, baseline.AcceptedAt)); return true;
            });
        Accounting = new AccountingStore(directory, access, new FixtureClock(), Database);
        if (initialize)
        {
            Accounting.Execute(baseline.Consumer, Command("configure", "fixture-account-config", baseline) with { CreditLimit = credit }, "fixture");
            Accounting.Execute(baseline.Consumer, Command("tariff", "fixture-live-v8-tariff", baseline) with { UnitPrice = livePrice }, "fixture");
        }
    }
    public BillingCommand Command(string operation, string key, FixtureEvent message) => new(
        Accounting.Read(message.Consumer).Account.Revision, operation, key, "postpaid", message.Currency, 10000,
        message.Product, message.Meter, message.UnitPrice, message.Quantity, message.Amount, "synthetic fixture", null, null);
    public void Dispose() => Database.Dispose();
}
