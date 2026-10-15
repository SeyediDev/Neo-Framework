using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.UnifiedPortal.Web;

var subject = "a80bfdbd-31fe-47c7-a8da-e9b4fd3187ad";
var consumer = Guid.Parse("fba1c383-28a9-4b54-a05b-336b442737b3");
var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
var to = from.AddDays(14);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
JsonObject Envelope() => new()
{
    ["subject"] = subject,
    ["report"] = new JsonObject
    {
        ["consumerTenantId"] = consumer.ToString(), ["from"] = from.ToString("O"), ["to"] = to.ToString("O"),
        ["asOf"] = to.AddHours(1).ToString("O"), ["status"] = "ready", ["basis"] = "posted-ledger",
        ["currency"] = "USD", ["totalAmount"] = "9007199254740993.10",
        ["items"] = new JsonArray
        {
            new JsonObject { ["productKey"] = "service-a", ["meterId"] = "tokens", ["meterVersion"] = "1", ["unit"] = "token",
                ["quantity"] = "1.2500", ["amount"] = "9007199254740993.01", ["chargeCount"] = 1 },
            new JsonObject { ["productKey"] = "service-a", ["meterId"] = "legacy", ["meterVersion"] = null, ["unit"] = null,
                ["quantity"] = null, ["amount"] = "0.09", ["chargeCount"] = 2 }
        }
    }
};
ConsumerUsageView Read(JsonObject data) => ConsumerUsageReportProjection.Read(Encoding.UTF8.GetBytes(data.ToJsonString()),
    subject, consumer, from, to, new Dictionary<string, string> { ["service-a"] = "Approved service", ["invented-service"] = "Not in report" });
void Rejected(JsonObject data, string name)
{
    var view = Read(data);
    Check(view.Status == "unavailable" && view.Items.Count == 0 && view.Currency is null
        && view.TotalAmount is null && view.AsOf is null && view.ServiceCount is null, name);
    Check(view.OrganizationId == consumer && view.From == from && view.To == to, name + " preserves requested scope");
}
var valid = Read(Envelope());
Check(valid.Status == "ready" && valid.TotalAmount == "9007199254740993.10", "money above JavaScript integer precision remains exact text");
Check(valid.Items[0].Quantity == "1.2500" && valid.Items[1].Unit is null && valid.Items[1].Quantity is null,
    "measured precision and unknown legacy units are preserved");
Check(valid.ServiceCount == 1 && valid.Items.Count == 2 && valid.Items.All(x => x.Name == "Approved service"),
    "metrics count distinct products and approved name lookup adds no rows");
var withPrivateEvidence = Envelope(); withPrivateEvidence["report"]!["reasonCode"] = "private-ledger-evidence";
Check(!JsonSerializer.Serialize(Read(withPrivateEvidence)).Contains("private-ledger-evidence"), "central internal fields are never projected");
var serialized = JsonSerializer.Serialize(valid, new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(!serialized.Contains(subject) && !serialized.Contains("reasonCode") && !serialized.Contains("quota"),
    "browser projection contains no subject, internal reason or invented quota");
foreach (var status in new[] { "notRecorded", "incomplete", "unavailable" })
{
    var data = Envelope(); var report = data["report"]!.AsObject();
    report["status"] = status; report["currency"] = null; report["totalAmount"] = null; report["items"] = new JsonArray();
    var view = Read(data);
    Check(view.Status == status && view.TotalAmount is null && view.ServiceCount is null, status + " is unknown, not zero");
    report["totalAmount"] = "0"; Rejected(data, status + " cannot contain partial financial data");
}
var empty = Envelope(); var emptyReport = empty["report"]!.AsObject(); emptyReport["totalAmount"] = "0"; emptyReport["items"] = new JsonArray();
Check(Read(empty) is { Status: "ready", TotalAmount: "0", ServiceCount: 0 }, "a recorded empty account has a real zero");
foreach (var change in new (string Name, Action<JsonObject> Mutate)[]
{
    ("foreign subject", x => x["subject"] = Guid.NewGuid().ToString()),
    ("case-only subject mismatch", x => x["subject"] = subject.ToUpperInvariant()),
    ("foreign consumer", x => x["report"]!["consumerTenantId"] = Guid.NewGuid().ToString()),
    ("wrong from", x => x["report"]!["from"] = from.AddTicks(1).ToString("O")),
    ("wrong to", x => x["report"]!["to"] = to.AddTicks(-1).ToString("O")),
    ("non-UTC equivalent boundary", x => x["report"]!["from"] = from.ToOffset(TimeSpan.FromHours(1)).ToString("O")),
    ("non-UTC snapshot", x => x["report"]!["asOf"] = to.ToOffset(TimeSpan.FromHours(1)).ToString("O")),
    ("missing UTC designator", x => x["report"]!["from"] = from.ToString("yyyy-MM-ddTHH:mm:ss")),
    ("unknown minus-zero offset", x => x["report"]!["from"] = from.ToString("yyyy-MM-ddTHH:mm:ss") + "-00:00"),
    ("wrong ledger basis", x => x["report"]!["basis"] = "estimate"),
    ("unrecognized status", x => x["report"]!["status"] = "pending"),
    ("unsupported currency", x => x["report"]!["currency"] = "BTC"),
    ("numeric total is not exact string contract", x => x["report"]!["totalAmount"] = 42),
    ("missing total", x => x["report"]!.AsObject().Remove("totalAmount")),
    ("mismatched totals", x => x["report"]!["totalAmount"] = "1"),
    ("null rows", x => x["report"]!["items"] = null),
    ("null row", x => x["report"]!["items"]!.AsArray().Add((JsonNode?)null)),
    ("duplicate group", x => x["report"]!["items"]!.AsArray().Add(x["report"]!["items"]![0]!.DeepClone())),
    ("missing quantity", x => x["report"]!["items"]![0]!.AsObject().Remove("quantity")),
    ("partial meter metadata", x => x["report"]!["items"]![0]!["unit"] = null),
    ("zero measured quantity", x => x["report"]!["items"]![0]!["quantity"] = "0"),
    ("quantity rounding", x => x["report"]!["items"]![0]!["quantity"] = "0.00000000000000000000000000001"),
    ("exponent quantity", x => x["report"]!["items"]![0]!["quantity"] = "1e2"),
    ("negative amount", x => x["report"]!["items"]![0]!["amount"] = "-1"),
    ("fractional cents", x => x["report"]!["items"]![0]!["amount"] = "1.001"),
    ("missing charges", x => x["report"]!["items"]![0]!["chargeCount"] = 0),
    ("aggregate charge limit", x => x["report"]!["items"]![0]!["chargeCount"] = 10000),
    ("unsafe label", x => x["report"]!["items"]![0]!["productKey"] = "service\nforged")
}) { var data = Envelope(); change.Mutate(data); Rejected(data, change.Name); }
foreach (var raw in new[] { "null", "{}", "[1]", "not-json", Envelope().ToJsonString().Replace("\"subject\":", "\"subject\":\"ignored\",\"subject\":"),
    Envelope().ToJsonString().Replace("\"status\":", "\"Status\":\"ready\",\"status\":") })
{
    var view = ConsumerUsageReportProjection.Read(Encoding.UTF8.GetBytes(raw), subject, consumer, from, to);
    Check(view.Status == "unavailable" && view.Items.Count == 0, "malformed/duplicate JSON rejected");
}
Check(ConsumerUsageReportProjection.Read(new byte[ConsumerUsageReportProjection.MaximumBytes + 1], subject, consumer, from, to).Status == "unavailable",
    "8MiB transport bound rejects oversized responses");
var ir = Envelope(); var irReport = ir["report"]!.AsObject(); irReport["currency"] = "IRR"; irReport["totalAmount"] = "1.01";
Rejected(ir, "IRR has no fractional minor units");
var many = Envelope(); var manyRows = new JsonArray();
for (var i = 0; i < 10001; i++) manyRows.Add(Envelope()["report"]!["items"]![0]!.DeepClone());
many["report"]!["items"] = manyRows;
Rejected(many, "10000-row report limit rejects oversized item arrays");
var free = Envelope(); var freeReport = free["report"]!.AsObject(); freeReport["totalAmount"] = "0";
freeReport["items"]!.AsArray().RemoveAt(1); freeReport["items"]![0]!["amount"] = "0";
Check(Read(free) is { Status: "ready", ServiceCount: 1, TotalAmount: "0" }, "positive measured usage may have a real zero posted charge");
foreach (var invalid in new (Guid Consumer, DateTimeOffset From, DateTimeOffset To)[]
{
    (Guid.Empty, from, to), (consumer, to, from), (consumer, from, from),
    (consumer, from.ToOffset(TimeSpan.FromHours(1)), to), (consumer, from, to.ToOffset(TimeSpan.FromHours(1)))
})
{
    try { ConsumerUsageReportProjection.Read(ReadOnlyMemory<byte>.Empty, subject, invalid.Consumer, invalid.From, invalid.To); throw new Exception("Expected argument rejection"); }
    catch (ArgumentException) { Check(true, "invalid requested consumer/period is rejected before parsing"); }
}
try { ConsumerUsageReportProjection.Read(ReadOnlyMemory<byte>.Empty, subject, consumer, from, from.AddDays(366)); throw new Exception("Expected boundary rejection"); }
catch (ArgumentException) { Check(true, "366-day duration is rejected per HTTP v1 contract"); }
var precise = ConsumerUsageReportProjection.Read(ReadOnlyMemory<byte>.Empty, subject, consumer, from.AddTicks(1), to.AddTicks(-1));
Check(precise.PeriodLabel.Contains("00:00:00.0000001") && precise.PeriodLabel.Contains("23:59:59.9999999"), "period label keeps subsecond UTC boundaries");
Console.WriteLine($"{checks} usage-projection checks passed; synthetic v1 JSON only, no live authorization or finance integration claimed.");
