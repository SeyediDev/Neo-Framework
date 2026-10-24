using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.Data.Sqlite;

static class ConsumerReportChecks
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = To.AddHours(8);
    private static int checks;

    public static int Main()
    {
        try
        {
            EmptyAndIsolation(); PeriodAndKinds(); ExactAmountsAndMeasured(); ExactSumPreservation(); CorruptAccounts(); CorruptBindings(); BoundsAndTransactions();
            Console.WriteLine($"PASS consumer usage report: {checks} checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++; Console.WriteLine("PASS consumer usage report: " + name);
    }

    private static decimal Number(string? text) => decimal.Parse(text ?? throw new Exception("Missing exact decimal text."), CultureInfo.InvariantCulture);

    private static void EmptyAndIsolation()
    {
        using var f = new Fixture();
        var absent = f.Read();
        Check(absent.Status == "notRecorded" && absent.TotalAmount is null && absent.Currency is null && absent.Items.Count == 0,
            "absent accounting is not a manufactured zero account");
        Check(absent.ConsumerTenantId == f.Consumer && absent.From == From && absent.To == To && absent.AsOf == Now && absent.Basis == "posted-ledger",
            "report retains consumer, UTC period, as-of time and ledger basis");
        f.Account([]);
        var empty = f.Read();
        Check(empty.Status == "ready" && empty.TotalAmount == "0" && empty.Currency == "IRR" && empty.Items.Count == 0,
            "an explicitly persisted empty ledger is a ready zero");
        f.Account([Charge("own", 10)]);
        f.Account([Charge("foreign", 999)], tenant: f.Other);
        Check(Number(f.Read().TotalAmount) == 10 && Number(f.Read(f.Other).TotalAmount) == 999,
            "different consumers never share accounting totals");
    }

    private static void PeriodAndKinds()
    {
        using var f = new Fixture();
        f.Account([
            Charge("from", 2, occurred: From), Charge("inside", 3),
            Charge("before", 4, occurred: From.AddTicks(-1)), Charge("to", 7, occurred: To),
            Charge("late-post", 10, at: Now, occurred: From.AddDays(2)),
            Charge("legacy-post-time", 5, at: From.AddDays(4)) with { UsageOccurredAt = null },
            Charge("credit", 500) with { Kind = "credit" }, Charge("payment", 500) with { Kind = "payment" },
            Charge("receipt", 500) with { Kind = "receipt" }
        ]);
        var pending = Event(f, "pending-only");
        f.Operation(pending, status: "accounting-pending");
        f.Database.Transaction(() => { f.Database.Emit("pending-evidence", "UsageAccountingPending", new { TenantId = f.Consumer, Amount = 10000 }, Now); return true; });
        var report = f.Read();
        Check(report.Status == "ready" && Number(report.TotalAmount) == 20 && report.Items.Single().ChargeCount == 4,
            "half-open occurrence period includes delayed posts and uses registration time for legacy rows");
        Check(report.Items.Single().Quantity is null && report.Items.Single().Unit is null && report.Items.Single().MeterVersion is null,
            "legacy costs do not invent a known unit, version or quantity");
        Check(Number(report.TotalAmount) == 20, "credits, payments, receipts and pending evidence do not become usage costs");
    }

    private static void ExactAmountsAndMeasured()
    {
        using var exact = new Fixture();
        exact.Account([Charge("large-usd", 9007199254740993.01m), Charge("fraction", .09m)], currency: "USD");
        var large = exact.Read();
        var serialized = JsonSerializer.SerializeToElement(large);
        Check(large.Status == "ready" && Number(large.TotalAmount) == 9007199254740993.10m
            && Number(large.Items.Single().Amount) == 9007199254740993.10m,
            "USD totals beyond JavaScript integer precision remain exact decimal text");
        Check(serialized.GetProperty("TotalAmount").ValueKind == JsonValueKind.String
            && serialized.GetProperty("Items")[0].GetProperty("Amount").ValueKind == JsonValueKind.String,
            "serialized financial quantities remain string tokens");
        using var f = new Fixture();
        var first = Event(f, "historical-first"); var second = Event(f, "historical-second");
        f.Operation(first); f.Operation(second);
        f.Account([MeasuredCharge(first), Receipt(first), MeasuredCharge(second), Receipt(second)],
            tariffs: [new("organization-fabric", "organization.change", 999999)]);
        var report = f.Read(); var item = report.Items.Single();
        Check(report.Status == "ready" && Number(report.TotalAmount) == 24 && item.ProductKey == "organization-fabric"
            && item.MeterId == "organization.change" && item.MeterVersion == "1" && item.Unit == "change"
            && item.Quantity == "2" && Number(item.Amount) == 24 && item.ChargeCount == 2,
            "committed frozen evidence supplies quantity, unit and historical cost with different posting and commit clocks");
        Check(f.Database.Read<BillingAccount>("accounting", f.Consumer.ToString())!.Tariffs.Single().UnitPrice == 999999
            && Number(report.TotalAmount) == 24, "current tariffs cannot recalculate historical posted charges");
        f.Account([MeasuredCharge(first), Charge("legacy-same-meter", 5)]);
        var mixed = f.Read();
        Check(mixed.Items.Count == 2 && mixed.Items.Single(x => x.Unit is null).Quantity is null
            && mixed.Items.Single(x => x.Unit == "change").Quantity == "1" && Number(mixed.TotalAmount) == 17,
            "legacy unknown units remain separate from measured units in the same product and meter");
    }

    private static void ExactSumPreservation()
    {
        const decimal whole = 79228162514264337593543950334m;
        using var sameGroup = new Fixture();
        sameGroup.Account([Charge("large-same-group", whole), Charge("cent-same-group", .01m)], currency: "USD");
        Unavailable(sameGroup, "same-group sum that cannot preserve the posted cent");

        using var differentProduct = new Fixture();
        differentProduct.Account([Charge("large-product", whole), Charge("cent-product", .01m) with { ProductKey = "other-product" }], currency: "USD");
        Unavailable(differentProduct, "report total across products that cannot preserve the posted cent");

        using var differentMeter = new Fixture();
        differentMeter.Account([Charge("large-meter", whole), Charge("cent-meter", .01m) with { Metric = "other-meter" }], currency: "USD");
        Unavailable(differentMeter, "report total across meters that cannot preserve the posted cent");

        const decimal largeExact = 792281625142643375935439503.34m;
        const decimal expected = 792281625142643375935439503.35m;
        using var valid = new Fixture();
        valid.Account([Charge("large-exact", largeExact), Charge("cent-exact", .01m)], currency: "USD");
        var groupReport = valid.Read();
        Check(groupReport.Status == "ready" && Number(groupReport.TotalAmount) == expected
            && Number(groupReport.Items.Single().Amount) == expected && groupReport.Items.Single().ChargeCount == 2,
            "large exactly representable same-group sum retains every posted cent");
        valid.Account([Charge("large-exact-product", largeExact), Charge("cent-exact-product", .01m) with { ProductKey = "other-product", Metric = "other-meter" }], currency: "USD");
        var totalReport = valid.Read();
        Check(totalReport.Status == "ready" && Number(totalReport.TotalAmount) == expected && totalReport.Items.Count == 2
            && Number(totalReport.Items.Single(x => x.ProductKey == "organization-fabric").Amount) == largeExact
            && Number(totalReport.Items.Single(x => x.ProductKey == "other-product").Amount) == .01m,
            "large exactly representable total across groups retains each posted amount");
    }

    private static void CorruptAccounts()
    {
        var corruptions = new (string Name, Func<Fixture, string> Json)[]
        {
            ("malformed JSON", _ => "{"), ("null document", _ => "null"),
            ("duplicate account property", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10)])).Replace("\"Revision\":1", "\"Revision\":1,\"Revision\":1", StringComparison.Ordinal)),
            ("cross-consumer account", f => JsonSerializer.Serialize(Account(f.Other, [Charge("one", 10)]))),
            ("null ledger", f => JsonSerializer.Serialize(Account(f.Consumer, null!))),
            ("null ledger row", f => JsonSerializer.Serialize(Account(f.Consumer, [null!]))),
            ("negative revision", f => JsonSerializer.Serialize(Account(f.Consumer, []) with { Revision = -1 })),
            ("unsupported currency", f => JsonSerializer.Serialize(Account(f.Consumer, []) with { Currency = "GBP" })),
            ("negative usage amount", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("negative", -1)]))),
            ("zero usage quantity", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("zero-quantity", 10) with { Quantity = 0 }]))),
            ("duplicate charge identity", f => { var row = Charge("one", 10); return JsonSerializer.Serialize(Account(f.Consumer, [row, row with { Key = "two" }])); }),
            ("duplicate usage key", f => { var row = Charge("one", 10); return JsonSerializer.Serialize(Account(f.Consumer, [row, row with { Id = Guid.NewGuid() }])); }),
            ("duplicate nested ledger property", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10)])).Replace("\"Amount\":10", "\"Amount\":10,\"Amount\":10", StringComparison.Ordinal)),
            ("unrepresentable raw amount", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10)])).Replace("\"Amount\":10", "\"Amount\":1.00000000000000000000000000001", StringComparison.Ordinal)),
            ("unrepresentable raw quantity", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10)])).Replace("\"Quantity\":1", "\"Quantity\":1.00000000000000000000000000001", StringComparison.Ordinal)),
            ("exponent raw amount", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10)])).Replace("\"Amount\":10", "\"Amount\":1e1", StringComparison.Ordinal)),
            ("unknown ledger kind", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("one", 10) with { Kind = "usgae" }]))),
            ("fraction outside currency scale", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("fraction", .5m)]))),
            ("overflowing posted sum", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("maximum", decimal.MaxValue), Charge("overflow", 1)]))),
            ("copied organization row", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("organization:" + f.Other + ":1", 10)]))),
            ("malformed organization revision", f => JsonSerializer.Serialize(Account(f.Consumer, [Charge("organization:" + f.Consumer + ":0", 10)])))
        };
        foreach (var test in corruptions)
        {
            using var f = new Fixture(); f.Raw("accounting", f.Consumer.ToString(), test.Json(f)); Unavailable(f, test.Name);
        }
        using var valid = new Fixture();
        valid.Account([Charge("organization:" + valid.Consumer + ":7", 10)]);
        Check(valid.Read().Status == "ready", "proper consumer-bound organization charge key remains readable");
        valid.Account([Charge("legacy-unknown-price", 10) with { UnitPrice = null }]);
        Check(valid.Read().Status == "ready" && valid.Read().Items.Single().Unit is null,
            "legacy null unit price does not invent a tariff or reject a posted cost");
    }

    private static void CorruptBindings()
    {
        var changes = new (string Name, Func<LedgerEntry, LedgerEntry> Change)[]
        {
            ("unstable charge ID", x => x with { Id = Guid.NewGuid() }), ("wrong product", x => x with { ProductKey = "other-product" }),
            ("wrong meter", x => x with { Metric = "other-meter" }), ("wrong quantity", x => x with { Quantity = 2 }),
            ("wrong posted amount", x => x with { Amount = 13 }), ("wrong frozen unit price", x => x with { UnitPrice = 13 }),
            ("wrong actor", x => x with { Actor = "other-subject" }), ("wrong evidence reference", x => x with { Reference = "other-reference" }),
            ("wrong occurrence time", x => x with { UsageOccurredAt = x.UsageOccurredAt!.Value.AddSeconds(1) }),
            ("unexpected invoice", x => x with { InvoiceId = Guid.NewGuid() })
        };
        foreach (var test in changes)
        {
            using var f = new Fixture(); var usage = Event(f, test.Name); f.Operation(usage); f.Account([test.Change(MeasuredCharge(usage))]); Unavailable(f, test.Name);
        }
        foreach (var test in new[] { "missing operation", "pending operation", "committed operation error", "wrong operation fingerprint", "wrong operation amount", "inexact operation amount", "wrong operation key", "null commit time", "invalid operation timeline", "malformed event", "duplicate event property", "cross-consumer event", "currency mismatch" })
        {
            using var f = new Fixture(); var usage = Event(f, test); f.Account([MeasuredCharge(usage)]);
            var operation = Operation(usage);
            switch (test)
            {
                case "missing operation": break;
                case "pending operation": f.PutOperation(usage.BusinessKey, operation with { Status = "accounting-pending" }); break;
                case "committed operation error": f.PutOperation(usage.BusinessKey, operation with { Error = "unresolved error" }); break;
                case "wrong operation fingerprint": f.PutOperation(usage.BusinessKey, operation with { Fingerprint = UsageEventV1.Hash("wrong") }); break;
                case "wrong operation amount": f.PutOperation(usage.BusinessKey, operation with { Amount = 13 }); break;
                case "inexact operation amount": f.Raw("measured.operation", usage.BusinessKey, JsonSerializer.Serialize(operation).Replace("\"Amount\":12", "\"Amount\":12.00000000000000000000000000001", StringComparison.Ordinal)); break;
                case "wrong operation key": f.PutOperation(usage.BusinessKey, operation with { BusinessKey = "measured:v1:wrong" }); break;
                case "null commit time": f.PutOperation(usage.BusinessKey, operation with { CommittedAt = null }); break;
                case "invalid operation timeline": f.PutOperation(usage.BusinessKey, operation with { ReceivedAt = Now }); break;
                case "malformed event": f.PutOperation(usage.BusinessKey, operation with { EventJson = "{" }); break;
                case "duplicate event property": f.PutOperation(usage.BusinessKey, operation with { EventJson = operation.EventJson.Replace("\"schemaVersion\":\"1.0\"", "\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"", StringComparison.Ordinal) }); break;
                case "cross-consumer event": f.PutOperation(usage.BusinessKey, operation with { EventJson = (usage with { ConsumerTenantId = f.Other }).ToWireJson() }); break;
                case "currency mismatch": f.PutOperation(usage.BusinessKey, operation with { EventJson = (usage with { RateSnapshot = usage.RateSnapshot with { Currency = "USD", MinorUnitScale = 2 } }).ToWireJson() }); break;
            }
            Unavailable(f, test);
        }
        using var outside = new Fixture();
        var baseline = Event(outside, "outside-period");
        var historical = baseline with { OccurredAt = From.AddSeconds(-1), ProducedAt = From,
            Operation = baseline.Operation with { AcceptedAt = From.AddMinutes(-1) }, RateSnapshot = baseline.RateSnapshot with { EffectiveFrom = From.AddDays(-1) } };
        outside.Operation(historical); outside.Account([MeasuredCharge(historical) with { Actor = "wrong-outside-subject" }]);
        Unavailable(outside, "invalid binding outside requested period");
    }

    private static void BoundsAndTransactions()
    {
        using var f = new Fixture(); f.Account([Charge("one", 1), Charge("two", 2), Charge("three", 3)]);
        var bounded = f.Read(maxRows: 2);
        Check(bounded.Status == "incomplete" && bounded.TotalAmount is null && bounded.Currency is null && bounded.Items.Count == 0,
            "scan limit does not expose a plausible partial total");
        Check(f.Read(maxRows: 3).Status == "ready" && Number(f.Read(maxRows: 3).TotalAmount) == 6,
            "inclusive row bound returns complete totals when every row is scanned");
        foreach (var size in new[] { 0, 10001 }) Throws<ArgumentOutOfRangeException>(() => f.Store.Read(f.Consumer, From, To, size), "invalid scan bound " + size);
        Throws<ArgumentException>(() => f.Store.Read(Guid.Empty, From, To), "empty consumer rejected");
        Throws<ArgumentException>(() => f.Store.Read(f.Consumer, From, From), "empty half-open period rejected");
        Throws<ArgumentException>(() => f.Store.Read(f.Consumer, To, From), "reversed period rejected");
        Throws<ArgumentException>(() => f.Store.Read(f.Consumer, From, From.AddDays(367)), "period exceeding 366 days rejected");
        Throws<ArgumentException>(() => f.Store.Read(f.Consumer, From.ToOffset(TimeSpan.FromHours(1)), To), "non-UTC lower bound rejected");
        Throws<ArgumentException>(() => f.Store.Read(f.Consumer, From, To.ToOffset(TimeSpan.FromHours(1))), "non-UTC upper bound rejected");
        Check(f.Store.Read(f.Consumer, From, From.AddDays(366)).Status == "ready", "366-day UTC period is within the contract");
        var before = f.Snapshot();
        Throws<InvalidOperationException>(() => f.Database.Transaction(() => f.Store.Read(f.Consumer, From, To)), "report cannot inspect an ambient uncommitted writer");
        Check(f.Snapshot() == before && !f.Database.IsInTransaction, "rejected writer read clears its transaction and changes no persisted state");
        Throws<SqliteException>(() => f.Database.ReadSnapshot(() => { f.Database.Put("read-test", "forbidden", new { Value = 1 }); return true; }),
            "Fabric read snapshot enforces query-only writes");
        Check(f.Snapshot() == before && !f.Database.IsInTransaction, "query-only rejection leaves documents and outbox unchanged");
        using var oversized = new Fixture();
        oversized.Raw("accounting", oversized.Consumer.ToString(), "{\"padding\":\"" + new string('x', 8 * 1024 * 1024) + "\"}");
        var limited = oversized.Read();
        Check(limited.Status == "incomplete" && limited.TotalAmount is null && limited.Items.Count == 0,
            "oversized accounting document has no partial financial result");
    }

    private static void Unavailable(Fixture f, string name)
    {
        var report = f.Read();
        Check(report.Status == "unavailable" && report.TotalAmount is null && report.Currency is null && report.Items.Count == 0,
            name + " fails closed without totals or rows");
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Check(true, name); return; }
        throw new Exception(name + " did not reject the operation.");
    }

    private static BillingAccount Account(Guid tenant, LedgerEntry[] rows, string currency = "IRR", Tariff[]? tariffs = null)
        => new(tenant, 1, "postpaid", currency, decimal.MaxValue, tariffs ?? [], rows, []);
    private static LedgerEntry Charge(string key, decimal amount, DateTimeOffset? at = null, DateTimeOffset? occurred = null)
        => new(Guid.NewGuid(), key, "usage", "organization-fabric", "organization.change", 1, amount,
            at ?? From.AddDays(1), "consumer-subject", "historical-receipt", UnitPrice: 12, UsageOccurredAt: occurred ?? From.AddDays(1));
    private static UsageEventV1 Event(Fixture f, string id) => new("1.0", Guid.NewGuid(), From.AddDays(2), From.AddDays(2).AddSeconds(1),
        new("organization-backend", "product-backend", "report-test"), "development", f.Other, f.Consumer, "organization-fabric", "historical-contract", 7,
        "historical-allocation", "2026-10", new(id, "organization.unit.save/v1", "consumer-subject", Guid.NewGuid(), UsageEventV1.Hash("request:" + id), From.AddDays(2).AddSeconds(-1), "organization-unit-committed/v1"),
        new("decision:" + id, "policy/v7", "admission:" + id), new("organization.change", "1", "change", "1"),
        new("historical-rate", 7, From, To, "12", "IRR", 0, "away-from-zero", "postpaid"), "completed",
        new("receipt:" + id, UsageEventV1.Hash("result:" + id), "fabric://receipt/" + id), Guid.NewGuid());
    private static MeasuredUsageOperation Operation(UsageEventV1 usage)
        => new(usage.BusinessKey, usage.OperationFingerprint, usage.ToWireJson(), "committed", usage.Amount, Now.AddMinutes(-4), Now.AddMinutes(-1), null);
    private static LedgerEntry MeasuredCharge(UsageEventV1 usage)
        => new(UsageEventV1.StableGuid(usage.BusinessKey + ":charge"), usage.BusinessKey, "usage", usage.ProductKey, usage.Meter.Id, usage.Quantity,
            usage.Amount, Now.AddMinutes(-3), usage.Operation.ActorId, usage.Evidence.Reference, UnitPrice: usage.UnitPrice, UsageOccurredAt: usage.OccurredAt);
    private static LedgerEntry Receipt(UsageEventV1 usage)
        => new(UsageEventV1.StableGuid(usage.BusinessKey + ":receipt"), usage.BusinessKey, "receipt", null, null, 0, 0, Now.AddMinutes(-3), usage.Operation.ActorId, usage.OperationFingerprint);

    private sealed class Fixture : IDisposable
    {
        public Guid Consumer { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fanasa-consumer-report-" + Guid.NewGuid().ToString("N"), "fabric.db");
        public FabricDatabase Database { get; }
        public ConsumerUsageReportStore Store { get; }
        public Fixture() { Database = new(Path); Store = new(Database, new FixedClock()); }
        public void Account(LedgerEntry[] rows, string currency = "IRR", Guid? tenant = null, Tariff[]? tariffs = null)
        {
            var consumer = tenant ?? Consumer;
            Database.Transaction(() => { Database.Put("accounting", consumer.ToString(), ConsumerReportChecks.Account(consumer, rows, currency, tariffs)); return true; });
        }
        public void Operation(UsageEventV1 usage, string status = "committed") => PutOperation(usage.BusinessKey, ConsumerReportChecks.Operation(usage) with { Status = status });
        public void PutOperation(string key, MeasuredUsageOperation operation) => Database.Transaction(() => { Database.Put("measured.operation", key, operation); return true; });
        public void Raw(string kind, string key, string json)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        public ConsumerUsageReport Read(Guid? consumer = null, int maxRows = 10000)
        {
            var before = Snapshot(); var report = Store.Read(consumer ?? Consumer, From, To, maxRows);
            Check(Snapshot() == before, "report read leaves all persisted documents and outbox unchanged"); return report;
        }
        public string Snapshot()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 'document',kind,key,json FROM fabric_documents UNION ALL SELECT 'outbox',CAST(id AS TEXT),key,kind || char(10) || payload || char(10) || recorded FROM fabric_outbox ORDER BY 1,2,3";
            using var reader = command.ExecuteReader(); var rows = new List<string[]>();
            while (reader.Read()) rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)]);
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() => Database.Dispose();
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
