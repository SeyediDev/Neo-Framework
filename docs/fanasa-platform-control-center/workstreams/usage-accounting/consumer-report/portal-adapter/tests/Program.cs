using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.UsageAccounting.PortalAdapter;
using Microsoft.Data.Sqlite;

static class PortalAdapterChecks
{
    private const string Subject = "private-subject-to-protect";
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = To.AddHours(8);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, WorkspaceConsumerUsage> Fixtures = [];
    private static int checks;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--export-fixtures"))
                throw new ArgumentException("Use --export-fixtures followed by a fresh output directory.");
            if (args.Length == 2 && (Directory.Exists(Path.GetFullPath(args[1])) || File.Exists(Path.GetFullPath(args[1]))))
                throw new IOException("The fixture output directory must not already exist.");
            StoreIntegration(); IdentityAndPeriod(); AdversarialReports(); ArgumentBoundaries(); Serialization();
            if (args.Length == 2) Export(Path.GetFullPath(args[1]));
            Console.WriteLine($"PASS Portal usage adapter: {checks} checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++; Console.WriteLine("PASS Portal usage adapter: " + name);
    }

    private static decimal Number(string? text) => decimal.Parse(text ?? throw new Exception("Missing decimal text."), CultureInfo.InvariantCulture);
    private static bool Hidden(WorkspaceConsumerUsage value) => value.TotalAmount is null && value.Currency is null && value.AsOf is null && value.Items.Count == 0;
    private static void Unavailable(WorkspaceConsumerUsage value, string name) => Check(value.Status == "unavailable" && Hidden(value), name + " clears financial and display data");

    private static void StoreIntegration()
    {
        using var f = new Fixture();
        var absent = f.Map(); Fixtures["not-recorded"] = absent;
        Check(absent.Status == "notRecorded" && Hidden(absent), "missing Fabric account remains unknown rather than zero");
        f.Account([]); var empty = f.Map(); Fixtures["ready-empty"] = empty;
        Check(empty.Status == "ready" && empty.TotalAmount == "0" && empty.Currency == "IRR" && empty.Items.Count == 0 && empty.AsOf is not null,
            "persisted empty Fabric account displays a known zero");
        f.Account([Legacy("private-legacy-ledger-key", 5)]);
        var legacy = f.Map(); Fixtures["ready-legacy"] = legacy;
        Check(legacy.Items.Single().Quantity is null && legacy.Items.Single().Unit is null && legacy.Items.Single().MeterVersion is null
            && legacy.Items.Single().Amount == "5" && legacy.Items.Single().ChargeCount == 1,
            "legacy costs retain unknown quantity and meter metadata");
        Check(legacy.Items.Single().Name == "organization-fabric", "product key is the fallback when no approved display name is passed");
        var usage = Usage(f, "private-operation"); f.Operation(usage);
        f.Account([Measured(usage), Receipt(usage)], tariffs: [new("organization-fabric", "organization.change", 999999)]);
        var approvedNames = new Dictionary<string, string> { ["organization-fabric"] = "Approved organization service", ["not-in-report"] = "Invisible lookup name" };
        var measured = f.Map(names: approvedNames); Fixtures["ready-measured"] = measured;
        var item = measured.Items.Single();
        Check(measured.Status == "ready" && item.Name == "Approved organization service" && item.Quantity == "1" && item.Unit == "change"
            && item.MeterVersion == "1" && item.Amount == "12" && measured.TotalAmount == "12" && item.ChargeCount == 1,
            "real committed report maps frozen meter metadata and approved names");
        Check(f.Database.Read<BillingAccount>("accounting", f.Consumer.ToString())!.Tariffs.Single().UnitPrice == 999999
            && measured.TotalAmount == "12", "changed current tariff does not recalculate displayed historical cost");
        Check(measured.Items.Count == 1, "approved lookup cannot manufacture a service catalog or additional rows");
        f.Account([Measured(usage), Receipt(usage), Legacy("private-mixed-ledger-key", 5)]);
        var mixed = f.Map(names: approvedNames); Fixtures["ready-mixed"] = mixed;
        Check(mixed.TotalAmount == "17" && mixed.Items.Count == 2 && mixed.Items.Single(x => x.Unit is null).Quantity is null
            && mixed.Items.Single(x => x.Unit == "change").Quantity == "1", "legacy and measured rows with the same meter remain distinct");
        f.Account([Legacy("large", 9007199254740993.01m), Legacy("fraction", .09m)], currency: "USD");
        var large = f.Map(); Fixtures["large-usd"] = large;
        Check(large.Status == "ready" && Number(large.TotalAmount) == 9007199254740993.10m && Number(large.Items.Single().Amount) == 9007199254740993.10m,
            "real USD ledger display preserves exact money above JavaScript integer precision");
        f.Account([Legacy("one", 1), Legacy("two", 2), Legacy("three", 3)]);
        var incomplete = f.Map(maxRows: 2); Fixtures["incomplete"] = incomplete;
        Check(incomplete.Status == "incomplete" && Hidden(incomplete), "bounded report displays no partial financial result");
        f.Raw("accounting", f.Consumer.ToString(), "{");
        var unavailable = f.Map(); Fixtures["unavailable"] = unavailable;
        Unavailable(unavailable, "corrupt Fabric account");
    }

    private static void IdentityAndPeriod()
    {
        using var f = new Fixture(); f.Account([Legacy("one", 12)]); var report = f.Read();
        WorkspaceConsumerUsage Map(ConsumerUsageReportEnvelope? envelope) => WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, envelope);
        Unavailable(Map(null), "absent trusted envelope");
        Unavailable(Map(new("another-subject", report)), "foreign envelope subject");
        Unavailable(Map(new(Subject.ToUpperInvariant(), report)), "case-only envelope subject mismatch");
        Unavailable(Map(new(Subject, report with { ConsumerTenantId = f.Other })), "foreign consumer envelope");
        Unavailable(Map(new(Subject, report with { From = From.AddTicks(1) })), "different lower period boundary");
        Unavailable(Map(new(Subject, report with { To = To.AddTicks(-1) })), "different upper period boundary");
        Unavailable(Map(new(Subject, report with { From = From.ToOffset(TimeSpan.FromHours(1)) })), "non-UTC envelope lower boundary");
        Unavailable(Map(new(Subject, report with { To = To.ToOffset(TimeSpan.FromHours(1)) })), "non-UTC envelope upper boundary");
        Unavailable(Map(new(Subject, null!)), "null typed report");
        var rejected = Map(new("other", report));
        Check(rejected.OrganizationId == f.Consumer && rejected.From == From && rejected.To == To && rejected.Basis == "posted-ledger",
            "rejected envelope cannot replace the requested display scope");
        var accepted = Map(new(Subject, report));
        Check(accepted.OrganizationId == f.Consumer && accepted.From == From && accepted.To == To && accepted.PeriodLabel.Length is > 0 and <= 80,
            "display preserves exact requested period with a short generated label");
        var preciseFrom = From.AddTicks(1); var preciseTo = To.AddTicks(-1);
        var precise = WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, preciseFrom, preciseTo, null);
        Check(precise.PeriodLabel.Contains("00:00:00.0000001", StringComparison.Ordinal)
            && precise.PeriodLabel.Contains("23:59:59.9999999", StringComparison.Ordinal) && precise.PeriodLabel.Contains("UTC", StringComparison.Ordinal),
            "nonmidnight period label preserves exact fractional UTC boundaries");
    }

    private static void AdversarialReports()
    {
        using var f = new Fixture(); f.Account([Legacy("one", 12)]); var valid = f.Read();
        var known = new ConsumerUsageItem("organization-fabric", "organization.change", "1", "change", "1", "12", 1);
        var legacy = valid.Items.Single();
        var changes = new (string Name, Func<ConsumerUsageReport, ConsumerUsageReport> Change)[]
        {
            ("unknown status", x => x with { Status = "pending" }), ("case-only status", x => x with { Status = "READY" }),
            ("unsupported basis", x => x with { Basis = "current-tariff" }), ("missing currency", x => x with { Currency = null }),
            ("non-UTC as-of time", x => x with { AsOf = Now.ToOffset(TimeSpan.FromHours(1)) }),
            ("unsupported currency", x => x with { Currency = "GBP" }), ("missing ready total", x => x with { TotalAmount = null }),
            ("negative total", x => x with { TotalAmount = "-12" }), ("exponent total", x => x with { TotalAmount = "1.2e1" }),
            ("inexact total precision", x => x with { TotalAmount = "12.00000000000000000000000000001" }),
            ("total differs from item sum", x => x with { TotalAmount = "13" }), ("null item collection", x => x with { Items = null! }),
            ("null item row", x => x with { Items = [null!] }),
            ("negative item amount", x => x with { Items = [legacy with { Amount = "-12" }] }),
            ("exponent item amount", x => x with { Items = [legacy with { Amount = "1.2e1" }] }),
            ("inexact item precision", x => x with { Items = [legacy with { Amount = "12.00000000000000000000000000001" }] }),
            ("empty product identity", x => x with { Items = [legacy with { ProductKey = "" }] }),
            ("empty meter identity", x => x with { Items = [legacy with { MeterId = "" }] }),
            ("zero charge count", x => x with { Items = [legacy with { ChargeCount = 0 }] }),
            ("negative charge count", x => x with { Items = [legacy with { ChargeCount = -1 }] }),
            ("excessive charge count", x => x with { Items = [legacy with { ChargeCount = 10001 }] }),
            ("aggregate charge count exceeds bound", x => x with { TotalAmount = "24", Items = [legacy with { ChargeCount = 5001 }, legacy with { ProductKey = "other-product", ChargeCount = 5001 }] }),
            ("duplicate group", x => x with { TotalAmount = "24", Items = [legacy, legacy] }),
            ("legacy version without unit or quantity", x => x with { Items = [legacy with { MeterVersion = "1" }] }),
            ("legacy unit without version or quantity", x => x with { Items = [legacy with { Unit = "change" }] }),
            ("legacy quantity without version or unit", x => x with { Items = [legacy with { Quantity = "1" }] }),
            ("zero measured quantity", x => x with { Items = [known with { Quantity = "0" }] }),
            ("negative measured quantity", x => x with { Items = [known with { Quantity = "-1" }] }),
            ("exponent measured quantity", x => x with { Items = [known with { Quantity = "1e0" }] }),
            ("inexact measured quantity", x => x with { Items = [known with { Quantity = "1.00000000000000000000000000001" }] })
        };
        foreach (var test in changes) Unavailable(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, test.Change(valid))), test.Name);
        var roundedTotal = valid with { Currency = "USD", TotalAmount = "79228162514264337593543950334", Items = [legacy with { Amount = "79228162514264337593543950334" }, legacy with { ProductKey = "other-product", Amount = "0.01" }] };
        Unavailable(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, roundedTotal)), "sum that would silently drop cents");
        var exactTotal = valid with { Currency = "USD", TotalAmount = "792281625142643375935439503.35", Items = [legacy with { Amount = "792281625142643375935439503.34" }, legacy with { ProductKey = "other-product", Amount = "0.01" }] };
        Check(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, exactTotal)).Status == "ready",
            "large exactly representable sum remains ready across groups");
        foreach (var status in new[] { "notRecorded", "incomplete", "unavailable" })
        {
            var empty = valid with { Status = status, Currency = null, TotalAmount = null, Items = [], ReasonCode = "private-internal-error" };
            var mapped = WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, empty));
            Check(mapped.Status == status && Hidden(mapped), status + " keeps its meaning without financial or internal error data");
            Unavailable(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, valid with { Status = status })),
                status + " cannot conceal an unexpected financial payload");
        }
        var free = valid with { TotalAmount = "0", Items = [known with { Amount = "0" }] };
        Check(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, free)).Items.Single().Quantity == "1",
            "a known positive quantity with zero posted cost is a valid usage row");
        var fractionalIrr = valid with { TotalAmount = "12.01", Items = [legacy with { Amount = "12.01" }] };
        Unavailable(WorkspaceUsageReportAdapter.Map(Subject, f.Consumer, From, To, new(Subject, fractionalIrr)), "IRR fractional minor units");
        var numericTotal = JsonSerializer.Serialize(valid).Replace("\"TotalAmount\":\"12\"", "\"TotalAmount\":12", StringComparison.Ordinal);
        Throws<JsonException>(() => JsonSerializer.Deserialize<ConsumerUsageReport>(numericTotal), "numeric JSON financial token cannot enter typed adapter contract");
    }

    private static void ArgumentBoundaries()
    {
        var consumer = Guid.NewGuid();
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map("", consumer, From, To, null), "empty expected subject rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(" ", consumer, From, To, null), "blank expected subject rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(null!, consumer, From, To, null), "null expected subject rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, Guid.Empty, From, To, null), "empty consumer rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, consumer, From, From, null), "empty display period rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, consumer, To, From, null), "reversed display period rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, consumer, From.ToOffset(TimeSpan.FromHours(1)), To, null), "non-UTC requested lower boundary rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, consumer, From, To.ToOffset(TimeSpan.FromHours(1)), null), "non-UTC requested upper boundary rejected");
        Throws<ArgumentException>(() => WorkspaceUsageReportAdapter.Map(Subject, consumer, From, From.AddDays(367), null), "display period exceeding 366 days rejected");
    }

    private static void Serialization()
    {
        foreach (var fixture in Fixtures)
        {
            var json = JsonSerializer.Serialize(fixture.Value, WebJson); using var document = JsonDocument.Parse(json); var root = document.RootElement;
            Check(root.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["organizationId", "status", "from", "to", "periodLabel", "asOf", "currency", "totalAmount", "items", "basis"]),
                fixture.Key + " exposes only display contract fields");
            Check(!json.Contains(Subject, StringComparison.Ordinal) && !json.Contains("private-", StringComparison.Ordinal)
                && !root.TryGetProperty("reasonCode", out _) && !root.TryGetProperty("quota", out _), fixture.Key + " excludes subject, raw keys, actors, errors and quota");
            if (fixture.Value.TotalAmount is not null) Check(root.GetProperty("totalAmount").ValueKind == JsonValueKind.String, fixture.Key + " money serializes as a string");
            foreach (var row in root.GetProperty("items").EnumerateArray())
            {
                Check(row.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["productKey", "name", "meterId", "meterVersion", "unit", "quantity", "amount", "chargeCount"]), "usage item excludes operational fields");
                Check(row.GetProperty("amount").ValueKind == JsonValueKind.String && row.GetProperty("quantity").ValueKind is JsonValueKind.String or JsonValueKind.Null,
                    "usage item amount is text and unknown quantity remains JSON null");
            }
        }
        using var f = new Fixture(); f.Account([Legacy("one", 12)]);
        var hostileName = "</script><script>alert(1)</script>";
        var mapped = f.Map(names: new Dictionary<string, string> { ["organization-fabric"] = hostileName });
        Check(mapped.Items.Single().Name == hostileName && !JsonSerializer.Serialize(mapped, WebJson).Contains("</script>", StringComparison.Ordinal),
            "caller-approved text remains data and default serialization escapes script delimiters");
        Check(f.Map(names: new Dictionary<string, string> { ["organization-fabric"] = "bad\nname" }).Items.Single().Name == "organization-fabric",
            "invalid approved label falls back to the valid product key");
    }

    private static void Export(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fixture export never overwrites an existing path.");
        Directory.CreateDirectory(path);
        foreach (var fixture in Fixtures) File.WriteAllText(Path.Combine(path, fixture.Key + ".json"), JsonSerializer.Serialize(fixture.Value, new JsonSerializerOptions(WebJson) { WriteIndented = true }));
        Console.WriteLine("Exported checked synthetic display fixtures to " + path);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { Check(true, name); return; } throw new Exception(name + " did not reject the operation.");
    }

    private static LedgerEntry Legacy(string key, decimal amount) => new(Guid.NewGuid(), key, "usage", "organization-fabric", "organization.change", 1, amount,
        From.AddDays(2), "private-legacy-actor", "private-legacy-reference", UsageOccurredAt: From.AddDays(2));
    private static UsageEventV1 Usage(Fixture f, string operation) => new("1.0", Guid.NewGuid(), From.AddDays(2), From.AddDays(2).AddSeconds(1),
        new("organization-backend", "product-backend", "adapter-test"), "development", f.Other, f.Consumer, "organization-fabric", "private-contract", 7,
        "private-allocation", "2026-10", new(operation, "organization.unit.save/v1", "private-measured-actor", Guid.NewGuid(), UsageEventV1.Hash("request:" + operation), From.AddDays(2).AddSeconds(-1), "organization-unit-committed/v1"),
        new("private-decision", "policy/v7", "private-admission"), new("organization.change", "1", "change", "1"),
        new("historical-rate", 7, From, To, "12", "IRR", 0, "away-from-zero", "postpaid"), "completed",
        new("private-receipt", UsageEventV1.Hash("result:" + operation), "fabric://private-reference"), Guid.NewGuid());
    private static LedgerEntry Measured(UsageEventV1 usage) => new(UsageEventV1.StableGuid(usage.BusinessKey + ":charge"), usage.BusinessKey, "usage", usage.ProductKey,
        usage.Meter.Id, usage.Quantity, usage.Amount, Now.AddMinutes(-3), usage.Operation.ActorId, usage.Evidence.Reference, UnitPrice: usage.UnitPrice, UsageOccurredAt: usage.OccurredAt);
    private static LedgerEntry Receipt(UsageEventV1 usage) => new(UsageEventV1.StableGuid(usage.BusinessKey + ":receipt"), usage.BusinessKey, "receipt", null, null, 0, 0,
        Now.AddMinutes(-3), usage.Operation.ActorId, usage.OperationFingerprint);

    private sealed class Fixture : IDisposable
    {
        public Guid Consumer { get; } = Guid.NewGuid(); public Guid Other { get; } = Guid.NewGuid();
        private string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "fanasa-portal-adapter-" + Guid.NewGuid().ToString("N"), "fabric.db");
        public FabricDatabase Database { get; } public ConsumerUsageReportStore Store { get; }
        public Fixture() { Database = new(DatabasePath); Store = new(Database, new FixedClock()); }
        public void Account(LedgerEntry[] rows, string currency = "IRR", Tariff[]? tariffs = null)
            => Database.Transaction(() => { Database.Put("accounting", Consumer.ToString(), new BillingAccount(Consumer, 1, "postpaid", currency, decimal.MaxValue, tariffs ?? [], rows, [])); return true; });
        public void Operation(UsageEventV1 usage)
            => Database.Transaction(() => { Database.Put("measured.operation", usage.BusinessKey, new MeasuredUsageOperation(usage.BusinessKey, usage.OperationFingerprint, usage.ToWireJson(), "committed", usage.Amount, Now.AddMinutes(-4), Now.AddMinutes(-1), null)); return true; });
        public ConsumerUsageReport Read(int maxRows = 10000) => Store.Read(Consumer, From, To, maxRows);
        public WorkspaceConsumerUsage Map(int maxRows = 10000, IReadOnlyDictionary<string, string>? names = null)
        {
            var before = Snapshot(); var report = Read(maxRows); var result = WorkspaceUsageReportAdapter.Map(Subject, Consumer, From, To, new(Subject, report), names);
            Check(Snapshot() == before, "Fabric report to display mapping changes no documents or outbox"); return result;
        }
        public void Raw(string kind, string key, string json)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        private SqliteConnection Open() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()); connection.Open(); return connection; }
        private string Snapshot()
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT 'document',kind,key,json FROM fabric_documents UNION ALL SELECT 'outbox',CAST(id AS TEXT),key,kind || char(10) || payload || char(10) || recorded FROM fabric_outbox ORDER BY 1,2,3";
            using var reader = command.ExecuteReader(); var rows = new List<string[]>();
            while (reader.Read()) rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)]); return JsonSerializer.Serialize(rows);
        }
        public void Dispose() => Database.Dispose();
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
