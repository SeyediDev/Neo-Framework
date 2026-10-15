using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.UnifiedPortal.Web;

var subject = "a80bfdbd-31fe-47c7-a8da-e9b4fd3187ad";
var consumer = Guid.Parse("fba1c383-28a9-4b54-a05b-336b442737b3");
var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
var to = from.AddDays(14);
var now = to.AddHours(2);
var origin = new Uri("https://platform.example.invalid/");
const string bearer = "synthetic-human-bearer-not-a-real-token";
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++; Console.WriteLine("PASS " + name);
}
ConsumerUsageHumanCredential Credential() => new(subject, bearer, now.AddMinutes(1));
JsonObject Envelope() => new()
{
    ["subject"] = subject,
    ["report"] = new JsonObject
    {
        ["consumerTenantId"] = consumer.ToString(), ["from"] = from.ToString("O"), ["to"] = to.ToString("O"),
        ["asOf"] = now.ToString("O"), ["status"] = "ready", ["basis"] = "posted-ledger",
        ["currency"] = "USD", ["totalAmount"] = "9007199254740993.01",
        ["items"] = new JsonArray
        {
            new JsonObject { ["productKey"] = "service-a", ["meterId"] = "tokens", ["meterVersion"] = "1",
                ["unit"] = "token", ["quantity"] = "1.2500", ["amount"] = "9007199254740993.01", ["chargeCount"] = 1 }
        }
    }
};
HttpResponseMessage Response(JsonObject? data = null)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent((data ?? Envelope()).ToJsonString(), Encoding.UTF8, "application/json")
    };
    response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
    return response;
}
RecordingHandler Handler(Func<HttpResponseMessage> response) => new((_, _) => Task.FromResult(response()));
void Empty(ConsumerUsageReadResult result, ConsumerUsageReadStatus status, string name)
{
    Check(result.Status == status && result.View.Status == "unavailable" && result.View.Items.Count == 0
        && result.View.TotalAmount is null && result.View.Currency is null && result.View.AsOf is null
        && result.View.ServiceCount is null, name + " clears financial data");
    Check(result.View.OrganizationId == consumer && result.View.From == from && result.View.To == to,
        name + " preserves requested consumer and UTC period");
}
var provider = new Provider(_ => ValueTask.FromResult<ConsumerUsageHumanCredential?>(Credential()));
var handler = Handler(() => Response());
using (var reader = new ConsumerUsageReportClient(origin, provider, handler, clock: new TestClock(now)))
{
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result is { Status: ConsumerUsageReadStatus.Success, View.Status: "ready" }
        && result.View.TotalAmount == "9007199254740993.01" && result.View.Items[0].Quantity == "1.2500",
        "v1 projection preserves exact financial strings");
    var request = handler.LastRequest!;
    Check(handler.Calls == 1 && provider.Calls == 1 && request.Method == HttpMethod.Get,
        "one human credential lookup and one GET, without retries");
    Check(request.RequestUri!.GetLeftPart(UriPartial.Path) == origin + "api/accounting/v1/consumers/" + consumer + "/usage-report"
        && Uri.UnescapeDataString(request.RequestUri.Query) == "?from=" + from.ToString("O") + "&to=" + to.ToString("O"),
        "only consumer and exact UTC period are sent to the fixed route");
    Check(!request.RequestUri.ToString().Contains(subject) && request.Headers.Authorization?.Parameter == bearer
        && request.Headers.Authorization.Scheme == "Bearer" && !request.Headers.Contains("Cookie")
        && request.Headers.CacheControl is { NoStore: true, NoCache: true }, "human bearer stays in request header only");
    var json = JsonSerializer.Serialize(result);
    Check(!json.Contains(subject) && !json.Contains(bearer) && !json.Contains("BearerToken"),
        "presentation result excludes subject and credential");
}
Check(!Credential().ToString().Contains(bearer) && JsonSerializer.Serialize(Credential()) == "{}",
    "credential does not leak through ToString or default JSON serialization");
using (var secure = ConsumerUsageReportClient.CreateHandler())
    Check(!secure.AllowAutoRedirect && !secure.UseCookies && secure.Credentials is null && !secure.PreAuthenticate
        && secure.AutomaticDecompression == DecompressionMethods.None && secure.MaxResponseHeadersLength == 32,
        "production handler disables redirects, cookies, ambient credentials and decompression");

foreach (var credential in new ConsumerUsageHumanCredential?[]
{
    null, new(subject, "", now.AddMinutes(1)), new(subject, "invalid\r\ntoken", now.AddMinutes(1)),
    new(subject, "token with space", now.AddMinutes(1)), new(subject, "تست", now.AddMinutes(1)),
    new(subject, new string('x', 32769), now.AddMinutes(1)), new(subject, bearer, now),
    new(subject, bearer, now.AddTicks(-1)), new(subject, bearer, now.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))),
    new("", bearer, now.AddMinutes(1)), new(Guid.Empty.ToString(), bearer, now.AddMinutes(1)),
    new(subject.ToUpperInvariant(), bearer, now.AddMinutes(1)), new("not-a-subject", bearer, now.AddMinutes(1))
})
{
    var rejectedHandler = Handler(() => Response());
    var rejectedProvider = new Provider(_ => ValueTask.FromResult(credential));
    using var reader = new ConsumerUsageReportClient(origin, rejectedProvider, rejectedHandler, clock: new TestClock(now));
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unauthorized, "missing/invalid/expired human credential");
    Check(rejectedHandler.Calls == 0, "invalid human credential never reaches the network");
}
foreach (var code in new[] { 300, 301, 302, 303, 307, 308, 400, 401, 403, 404, 429, 500, 503 })
{
    var rejectedHandler = Handler(() =>
    {
        var response = Response(); response.StatusCode = (HttpStatusCode)code;
        response.Headers.Location = new Uri("https://elsewhere.example.invalid/"); return response;
    });
    using var reader = new ConsumerUsageReportClient(origin, provider, rejectedHandler, clock: new TestClock(now));
    var expected = code switch { 400 => ConsumerUsageReadStatus.InvalidRequest, 401 => ConsumerUsageReadStatus.Unauthorized,
        403 => ConsumerUsageReadStatus.Forbidden, _ => ConsumerUsageReadStatus.Unavailable };
    Empty(await reader.ReadAsync(consumer, from, to), expected, "HTTP " + code);
    Check(rejectedHandler.Calls == 1, "HTTP " + code + " does not retry or follow Location");
}
foreach (var change in new (string Name, Action<HttpResponseMessage> Mutate)[]
{
    ("cacheable response", r => r.Headers.CacheControl = null),
    ("HTML response", r => r.Content.Headers.ContentType = new("text/html")),
    ("missing media type", r => r.Content.Headers.ContentType = null),
    ("encoded response", r => r.Content.Headers.ContentEncoding.Add("gzip")),
    ("oversized declared body", r => r.Content.Headers.ContentLength = ConsumerUsageReportProjection.MaximumBytes + 1L),
    ("foreign final URL", r => r.RequestMessage = new(HttpMethod.Get, "https://elsewhere.example.invalid/"))
})
{
    using var reader = new ConsumerUsageReportClient(origin, provider, Handler(() =>
    { var r = Response(); change.Mutate(r); return r; }), clock: new TestClock(now));
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, change.Name);
}
foreach (var change in new (string Name, Action<JsonObject> Mutate)[]
{
    ("foreign subject", x => x["subject"] = Guid.NewGuid().ToString()),
    ("foreign consumer", x => x["report"]!["consumerTenantId"] = Guid.NewGuid().ToString()),
    ("different period", x => x["report"]!["from"] = from.AddTicks(1).ToString("O")),
    ("non-ledger basis", x => x["report"]!["basis"] = "estimate"),
    ("partial financial report", x => x["report"]!["status"] = "incomplete")
})
{
    var data = Envelope(); change.Mutate(data);
    using var reader = new ConsumerUsageReportClient(origin, provider, Handler(() => Response(data)), clock: new TestClock(now));
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, change.Name);
}
foreach (var status in new[] { "notRecorded", "incomplete", "unavailable" })
{
    var data = Envelope(); var report = data["report"]!.AsObject(); report["status"] = status;
    report["currency"] = null; report["totalAmount"] = null; report["items"] = new JsonArray();
    using var reader = new ConsumerUsageReportClient(origin, provider, Handler(() => Response(data)), clock: new TestClock(now));
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result.View.Status == status && result.View.TotalAmount is null && result.View.ServiceCount is null
        && result.View.AsOf is null, status + " is preserved without stale amounts or fabricated zero");
}

foreach (var body in new[] { "not-json", new string(' ', ConsumerUsageReportProjection.MaximumBytes + 1) })
{
    using var reader = new ConsumerUsageReportClient(origin, provider, Handler(() =>
    {
        var response = Response(); response.Content = new StreamContent(new NonSeekStream(Encoding.UTF8.GetBytes(body)));
        response.Content.Headers.ContentType = new("application/json"); return response;
    }), clock: new TestClock(now));
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, "malformed or oversized unknown-length body");
}
var clock = new TestClock(now);
using (var reader = new ConsumerUsageReportClient(origin, provider, Handler(() =>
{
    var response = Response(); response.Content = new StreamContent(new NonSeekStream(
        Encoding.UTF8.GetBytes(Envelope().ToJsonString()), () => clock.Now = now.AddMinutes(2)));
    response.Content.Headers.ContentType = new("application/json"); return response;
}), clock: clock))
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unauthorized, "credential expiry while reading");

var responseNumber = 0;
using (var reader = new ConsumerUsageReportClient(origin, provider, Handler(() =>
{
    var response = Response();
    if (++responseNumber > 1) response.StatusCode = HttpStatusCode.Forbidden;
    return response;
}), clock: new TestClock(now)))
{
    Check((await reader.ReadAsync(consumer, from, to)).View.Status == "ready", "reused reader first receives a valid report");
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Forbidden,
        "revocation response on reused reader cannot retain an earlier amount");
}

using (var reader = new ConsumerUsageReportClient(origin, provider, new RecordingHandler((_, _) =>
    throw new HttpRequestException("synthetic private upstream details")), clock: new TestClock(now)))
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, "network error");
var slowProvider = new Provider(async ct => { await Task.Delay(Timeout.Infinite, ct); return Credential(); });
var unusedHandler = Handler(() => Response());
using (var reader = new ConsumerUsageReportClient(origin, slowProvider, unusedHandler,
    timeout: TimeSpan.FromMilliseconds(30), clock: new TestClock(now)))
{
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, "deadline includes credential lookup");
    Check(unusedHandler.Calls == 0, "credential timeout sends no network request");
}
using (var reader = new ConsumerUsageReportClient(origin, provider, new RecordingHandler(async (_, ct) =>
{
    await Task.Delay(Timeout.Infinite, ct); return Response();
}), timeout: TimeSpan.FromMilliseconds(30), clock: new TestClock(now)))
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, "deadline includes response headers");
using (var reader = new ConsumerUsageReportClient(origin, provider, Handler(() =>
{
    var response = Response(); response.Content = new StreamContent(new SlowStream());
    response.Content.Headers.ContentType = new("application/json"); return response;
}), timeout: TimeSpan.FromMilliseconds(30), clock: new TestClock(now)))
    Empty(await reader.ReadAsync(consumer, from, to), ConsumerUsageReadStatus.Unavailable, "deadline includes streaming response body");
using (var cancellation = new CancellationTokenSource())
using (var reader = new ConsumerUsageReportClient(origin, slowProvider, Handler(() => Response()), clock: new TestClock(now)))
{
    cancellation.CancelAfter(30);
    try { await reader.ReadAsync(consumer, from, to, cancellation.Token); throw new Exception("Expected caller cancellation"); }
    catch (OperationCanceledException) { Check(true, "caller cancellation is preserved, not a financial failure response"); }
}
using (var cancellation = new CancellationTokenSource())
{
    var unusedProvider = new Provider(_ => ValueTask.FromResult<ConsumerUsageHumanCredential?>(Credential()));
    using var reader = new ConsumerUsageReportClient(origin, unusedProvider, Handler(() => Response()));
    cancellation.Cancel();
    try { await reader.ReadAsync(consumer, from, to, cancellation.Token); throw new Exception("Expected early cancellation"); }
    catch (OperationCanceledException) { Check(unusedProvider.Calls == 0, "already-cancelled request does not acquire credentials"); }
}
foreach (var badOrigin in new[] { "http://platform.example.invalid/", "https://platform.example.invalid:8443/",
    "https://user:pass@platform.example.invalid/", "https://platform.example.invalid/path",
    "https://platform.example.invalid/?query=1", "https://platform.example.invalid/#fragment", "/relative" })
{
    using var badHandler = Handler(() => Response());
    try { using var reader = new ConsumerUsageReportClient(new Uri(badOrigin, UriKind.RelativeOrAbsolute), provider, badHandler);
        throw new Exception("Expected origin rejection"); }
    catch (ArgumentException) { Check(true, "invalid central origin rejected before network use"); }
}
foreach (var budget in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(61) })
{
    using var badHandler = Handler(() => Response());
    try { using var reader = new ConsumerUsageReportClient(origin, provider, badHandler, timeout: budget);
        throw new Exception("Expected timeout rejection"); }
    catch (ArgumentOutOfRangeException) { Check(true, "invalid timeout budget rejected"); }
}
foreach (var period in new[] { (Guid.Empty, from, to), (consumer, from, from), (consumer, to, from),
    (consumer, from, from.AddDays(366)), (consumer, from.ToOffset(TimeSpan.FromHours(1)), to) })
{
    var invalidProvider = new Provider(_ => ValueTask.FromResult<ConsumerUsageHumanCredential?>(Credential()));
    using var reader = new ConsumerUsageReportClient(origin, invalidProvider, Handler(() => Response()), clock: new TestClock(now));
    try { await reader.ReadAsync(period.Item1, period.Item2, period.Item3); throw new Exception("Expected scope rejection"); }
    catch (ArgumentException) { Check(invalidProvider.Calls == 0, "invalid consumer/period is rejected before credential lookup"); }
}
Console.WriteLine($"{checks} usage-transport checks passed; synthetic handlers only, no live SSO, entitlement or billing acceptance claimed.");

sealed class Provider(Func<CancellationToken, ValueTask<ConsumerUsageHumanCredential?>> get) : IConsumerUsageHumanCredentialProvider
{
    public int Calls { get; private set; }
    public ValueTask<ConsumerUsageHumanCredential?> GetAsync(CancellationToken cancellationToken) { Calls++; return get(cancellationToken); }
}
sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; LastRequest = request;
        var response = await send(request, cancellationToken); response.RequestMessage ??= request; return response;
    }
}
sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class NonSeekStream(byte[] bytes, Action? onRead = null) : Stream
{
    private readonly MemoryStream inner = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { onRead?.Invoke(); return inner.Read(buffer, offset, count); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { onRead?.Invoke(); return inner.ReadAsync(buffer, cancellationToken); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
sealed class SlowStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
