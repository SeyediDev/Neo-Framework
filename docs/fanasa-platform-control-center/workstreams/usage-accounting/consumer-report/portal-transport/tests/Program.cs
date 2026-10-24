using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.UsageAccounting.PortalTransport;

var origin = new Uri("https://platform.fanasa.net.local/");
var consumer = Guid.Parse("11111111-1111-1111-1111-111111111111");
var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);
var to = DateTimeOffset.Parse("2026-11-01T00:00:00Z", CultureInfo.InvariantCulture);
var now = DateTimeOffset.Parse("2026-10-15T00:00:00Z", CultureInfo.InvariantCulture);
var clock = new FixedClock(now);
const string subject = "human-1";
const string token = "synthetic-human-token";
var validCredential = new ValidatedHumanUsageCredential(subject, token, now.AddMinutes(10));
var groups = 0;
var assertions = 0;

void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception(message);
}

async Task Run(string name, Func<Task> scenario)
{
    await scenario();
    groups++;
    Console.WriteLine("PASS " + name);
}

string Fixture(string amount = "1.2500", bool legacy = false)
{
    return JsonSerializer.Serialize(new
    {
        subject,
        report = new
        {
            consumerTenantId = consumer,
            from, to, asOf = now,
            status = "ready", currency = "USD", totalAmount = amount,
            basis = "posted-ledger",
            items = new[]
            {
                new
                {
                    productKey = "product-a", meterId = "request",
                    meterVersion = legacy ? null : "v1",
                    unit = legacy ? null : "request",
                    quantity = legacy ? null : "1.0000",
                    amount, chargeCount = 2
                }
            }
        }
    });
}

JsonObject Report(JsonNode node) => node["report"]!.AsObject();
JsonObject Item(JsonNode node) => node["report"]!["items"]![0]!.AsObject();

HttpResponseMessage Response(
    string json, HttpStatusCode status = HttpStatusCode.OK)
{
    var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
    return response;
}

ConsumerUsageHttpReader Reader(
    FakeHandler handler, ValidatedHumanUsageCredential? credential = null,
    TimeSpan? timeout = null)
{
    return new(origin, new CredentialProvider(credential ?? validCredential),
        handler, timeout, clock);
}

void Cleared(ConsumerUsageReadResult result)
{
    var report = result.Envelope.Report;
    Check(report.Status == "unavailable", "Failure report status");
    Check(report.AsOf is null && report.Currency is null && report.TotalAmount is null,
        "Failure cleared timestamp/currency/amount");
    Check(report.Items.Count == 0, "Failure cleared rows");
    Check(report.ConsumerTenantId == consumer && report.From == from && report.To == to,
        "Failure retains caller scope only");
}

async Task Invalid(string name, string payload)
{
    await Run(name, async () =>
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(payload)));
        using var reader = Reader(handler);
        var result = await reader.ReadAsync(consumer, from, to);
        Check(result.TransportStatus == ConsumerUsageTransportStatus.Unavailable, name);
        Cleared(result);
        Check(handler.Calls == 1, "No retry");
    });
}

await Run("exact origin route, trusted bearer, no subject input or cookies", async () =>
{
    var handler = new FakeHandler((request, _) =>
    {
        Check(request.Method == HttpMethod.Get, "GET");
        Check(request.RequestUri!.GetLeftPart(UriPartial.Authority)
            == "https://platform.fanasa.net.local", "Exact origin");
        Check(request.RequestUri.AbsolutePath
            == $"/api/accounting/v1/consumers/{consumer:D}/usage-report", "Exact route");
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        Check(query.Contains(from.ToString("O", CultureInfo.InvariantCulture)), "Exact from");
        Check(query.Contains(to.ToString("O", CultureInfo.InvariantCulture)), "Exact to");
        Check(!query.Contains("subject") && !query.Contains("maxRows"), "No spoofing query");
        Check(request.Headers.Authorization?.Scheme == "Bearer"
            && request.Headers.Authorization.Parameter == token, "Trusted credential");
        Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Subject"),
            "No cookie or subject delegation");
        Check(request.Headers.CacheControl?.NoStore == true, "Request no-store");
        return Task.FromResult(Response(Fixture()));
    });
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result.TransportStatus == ConsumerUsageTransportStatus.Success, "Success");
    Check(result.Envelope.Subject == subject, "Subject bound");
    Check(result.Envelope.Report.TotalAmount == "1.2500", "Lexical money preserved");
    Check(result.Envelope.Report.Items[0].Quantity == "1.0000", "Quantity preserved");
    Check(handler.Calls == 1, "Single send");
});

await Run("production handler disables redirects, cookies and alternate authentication", () =>
{
    using var handler = ConsumerUsageHttpReader.CreateSecureHandler();
    Check(!handler.AllowAutoRedirect && !handler.UseCookies, "Redirect/cookie disabled");
    Check(handler.Credentials is null && !handler.PreAuthenticate, "No auth fallback");
    Check(handler.AutomaticDecompression == DecompressionMethods.None, "No hidden decompression");
    Check(!validCredential.ToString().Contains(token), "ToString redacted");
    Check(!JsonSerializer.Serialize(validCredential).Contains(token), "Token not serialized");
    return Task.CompletedTask;
});

await Run("large exact money and legacy null metadata retained", async () =>
{
    var handler = new FakeHandler((_, _) =>
        Task.FromResult(Response(Fixture("9007199254740993.0100", legacy: true))));
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result.TransportStatus == ConsumerUsageTransportStatus.Success, "Large money valid");
    Check(result.Envelope.Report.TotalAmount == "9007199254740993.0100", "No double rounding");
    var row = result.Envelope.Report.Items[0];
    Check(row.Quantity is null && row.Unit is null && row.MeterVersion is null,
        "Legacy unknown fields remain null");
});

await Run("real zero report remains ready", async () =>
{
    var fixture = JsonNode.Parse(Fixture())!;
    Report(fixture)["totalAmount"] = "0.0000";
    Report(fixture)["items"] = new JsonArray();
    var handler = new FakeHandler((_, _) => Task.FromResult(Response(fixture.ToJsonString())));
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result.TransportStatus == ConsumerUsageTransportStatus.Success, "Zero success");
    Check(result.Envelope.Report.Status == "ready"
        && result.Envelope.Report.TotalAmount == "0.0000", "Known zero");
});

foreach (var state in new[] { "notRecorded", "incomplete", "unavailable" })
{
    await Run(state + " clears server timestamp and preserves non-ready meaning", async () =>
    {
        var fixture = JsonNode.Parse(Fixture())!;
        var report = Report(fixture);
        report["status"] = state;
        report["currency"] = null;
        report["totalAmount"] = null;
        report["items"] = new JsonArray();
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(fixture.ToJsonString())));
        using var reader = Reader(handler);
        var result = await reader.ReadAsync(consumer, from, to);
        Check(result.TransportStatus == ConsumerUsageTransportStatus.Success, "Valid non-ready");
        Check(result.Envelope.Report.Status == state, "Status retained");
        Check(result.Envelope.Report.AsOf is null && result.Envelope.Report.Currency is null
            && result.Envelope.Report.TotalAmount is null
            && result.Envelope.Report.Items.Count == 0, "All finance cleared");
    });
}

foreach (var code in new[] { 301, 302, 303, 307, 308, 400, 401, 403, 503 })
{
    await Run("HTTP " + code + " never retries or follows Location", async () =>
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = Response("{\"internal\":\"secret\"}", (HttpStatusCode)code);
            response.Headers.Location = new Uri("https://untrusted.example/collect");
            return Task.FromResult(response);
        });
        using var reader = Reader(handler);
        var result = await reader.ReadAsync(consumer, from, to);
        var expected = code switch
        {
            400 => ConsumerUsageTransportStatus.InvalidRequest,
            401 => ConsumerUsageTransportStatus.Unauthorized,
            403 => ConsumerUsageTransportStatus.Forbidden,
            _ => ConsumerUsageTransportStatus.Unavailable
        };
        Check(result.TransportStatus == expected, "Sanitized status");
        Cleared(result);
        Check(handler.Calls == 1, "No resend");
        Check(!JsonSerializer.Serialize(result).Contains("secret"), "No server error leakage");
    });
}

var mutations = new (string Name, Action<JsonNode> Mutate)[]
{
    ("subject mismatch", node => node["subject"] = "another-human"),
    ("consumer mismatch", node => Report(node)["consumerTenantId"] = Guid.NewGuid().ToString()),
    ("from mismatch", node => Report(node)["from"] = "2026-10-02T00:00:00Z"),
    ("to mismatch", node => Report(node)["to"] = "2026-11-02T00:00:00Z"),
    ("non-UTC from", node => Report(node)["from"] = "2026-10-01T03:30:00+03:30"),
    ("non-UTC asOf", node => Report(node)["asOf"] = "2026-10-15T03:30:00+03:30"),
    ("missing ready asOf", node => Report(node)["asOf"] = null),
    ("foreign basis", node => Report(node)["basis"] = "estimated"),
    ("unknown status", node => Report(node)["status"] = "approved"),
    ("numeric total", node => Report(node)["totalAmount"] = 1.25m),
    ("numeric item amount", node => Item(node)["amount"] = 1.25m),
    ("numeric quantity", node => Item(node)["quantity"] = 1m),
    ("mismatched total", node => Report(node)["totalAmount"] = "2.50"),
    ("decimal loss", node => Item(node)["quantity"] =
        "0.000000000000000000000000000001"),
    ("exponent amount", node => Item(node)["amount"] = "1.25e0"),
    ("negative amount", node => Item(node)["amount"] = "-1.25"),
    ("mixed legacy metadata", node => Item(node)["meterVersion"] = null),
    ("zero measured quantity", node => Item(node)["quantity"] = "0"),
    ("zero charges", node => Item(node)["chargeCount"] = 0),
    ("oversized charges", node => Item(node)["chargeCount"] = 10001),
    ("fractional charges", node => Item(node)["chargeCount"] = 1.5m),
    ("duplicate group", node => Report(node)["items"]!.AsArray().Add(
        Item(node).DeepClone())),
    ("unknown/private field", node => Report(node)["reasonCode"] = "private-diagnostic"),
    ("non-ready financial payload", node => Report(node)["status"] = "notRecorded"),
    ("unsupported currency", node => Report(node)["currency"] = "ABC"),
    ("IRR minor precision", node => Report(node)["currency"] = "IRR"),
    ("missing required property", node => Report(node).Remove("basis"))
};
foreach (var mutation in mutations)
{
    var fixture = JsonNode.Parse(Fixture())!;
    mutation.Mutate(fixture);
    await Invalid(mutation.Name, fixture.ToJsonString());
}
await Invalid("malformed JSON", "{");
await Invalid("duplicate envelope property",
    Fixture().Replace("\"subject\":\"human-1\"",
        "\"subject\":\"human-1\",\"subject\":\"human-1\"", StringComparison.Ordinal));

await Run("advertised oversized body rejected before reading", async () =>
{
    var stream = new CountingStream(ConsumerUsageHttpReader.MaximumResponseBytes + 1);
    var handler = new FakeHandler((_, _) =>
    {
        var response = Response("");
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Content.Headers.ContentLength = ConsumerUsageHttpReader.MaximumResponseBytes + 1L;
        return Task.FromResult(response);
    });
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Cleared(result);
    Check(stream.BytesRead == 0, "No oversized read");
});

await Run("chunked oversized body bounded even without Content-Length", async () =>
{
    var stream = new CountingStream(ConsumerUsageHttpReader.MaximumResponseBytes + 9000);
    var handler = new FakeHandler((_, _) =>
    {
        var response = Response("");
        response.Content = new StreamContent(stream);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    });
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Cleared(result);
    Check(stream.BytesRead <= ConsumerUsageHttpReader.MaximumResponseBytes + 8192,
        "Bounded stream read");
});

foreach (var hazard in new[] { "html", "no-store-missing", "content-encoding", "different-origin" })
{
    await Run("response transport hazard " + hazard, async () =>
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = Response(Fixture());
            switch (hazard)
            {
                case "html":
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                    break;
                case "no-store-missing":
                    response.Headers.CacheControl = null;
                    break;
                case "content-encoding":
                    response.Content.Headers.ContentEncoding.Add("gzip");
                    break;
                default:
                    response.RequestMessage = new HttpRequestMessage(
                        HttpMethod.Get, "https://untrusted.example/collect");
                    break;
            }
            return Task.FromResult(response);
        });
        using var reader = Reader(handler);
        var result = await reader.ReadAsync(consumer, from, to);
        Cleared(result);
        Check(handler.Calls == 1, "No resend");
    });
}

await Run("missing or expired credential does not send", async () =>
{
    foreach (var credential in new ValidatedHumanUsageCredential?[]
    {
        null,
        new(subject, token, now),
        new(subject, "header\r\ninjection", now.AddMinutes(5))
    })
    {
        var handler = new FakeHandler((_, _) => throw new Exception("Must not send"));
        using var reader = new ConsumerUsageHttpReader(origin,
            new CredentialProvider(credential), handler, clock: clock);
        var result = await reader.ReadAsync(consumer, from, to);
        Check(result.TransportStatus == ConsumerUsageTransportStatus.Unauthorized,
            "Credential rejected");
        Cleared(result);
        Check(handler.Calls == 0, "No network");
    }
});

await Run("transport error sanitized without credential/error leakage", async () =>
{
    var handler = new FakeHandler((_, _) =>
        throw new HttpRequestException("synthetic-human-token private backend detail"));
    using var reader = Reader(handler);
    var result = await reader.ReadAsync(consumer, from, to);
    Cleared(result);
    var output = JsonSerializer.Serialize(result);
    Check(!output.Contains(token) && !output.Contains("private backend"), "No leakage");
});

await Run("request timeout clears finance", async () =>
{
    var handler = new FakeHandler(async (_, cancellation) =>
    {
        await Task.Delay(Timeout.Infinite, cancellation);
        return Response(Fixture());
    });
    using var reader = Reader(handler, timeout: TimeSpan.FromMilliseconds(25));
    var result = await reader.ReadAsync(consumer, from, to);
    Check(result.TransportStatus == ConsumerUsageTransportStatus.Unavailable, "Timeout status");
    Cleared(result);
    Check(handler.Calls == 1, "No timeout retry");
});

await Run("caller cancellation preserved before send", async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var handler = new FakeHandler((_, _) => throw new Exception("Must not send"));
    using var reader = Reader(handler);
    try
    {
        await reader.ReadAsync(consumer, from, to, cancellation.Token);
        throw new Exception("Cancellation swallowed");
    }
    catch (OperationCanceledException)
    {
        Check(handler.Calls == 0, "No send for cancelled caller");
    }
});

await Run("caller cancellation preserved during send", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var handler = new FakeHandler(async (_, cancellationToken) =>
    {
        cancellation.Cancel();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return Response(Fixture());
    });
    using var reader = Reader(handler);
    try
    {
        await reader.ReadAsync(consumer, from, to, cancellation.Token);
        throw new Exception("Cancellation swallowed");
    }
    catch (OperationCanceledException)
    {
        Check(handler.Calls == 1, "Caller cancellation propagated");
    }
});

await Run("invalid origin and period rejected before credential/send", async () =>
{
    foreach (var url in new[]
    {
        "http://platform.fanasa.net.local/",
        "https://platform.fanasa.net.local:8443/",
        "https://user:pass@platform.fanasa.net.local/",
        "https://platform.fanasa.net.local/path",
        "https://platform.fanasa.net.local/?x=1",
        "https://platform.fanasa.net.local/#fragment"
    })
    {
        using var handler = new FakeHandler((_, _) => throw new Exception("Must not send"));
        try
        {
            using var reader = new ConsumerUsageHttpReader(
                new Uri(url), new CredentialProvider(validCredential), handler, clock: clock);
            throw new Exception("Bad origin accepted");
        }
        catch (ArgumentException)
        {
            Check(handler.Calls == 0, "Bad origin rejected");
        }
    }

    var transport = new FakeHandler((_, _) => throw new Exception("Must not send"));
    using var validReader = Reader(transport);
    foreach (var range in new[]
    {
        (Consumer: Guid.Empty, From: from, To: to),
        (Consumer: consumer, From: from, To: from),
        (Consumer: consumer, From: from, To: from.AddDays(366)),
        (Consumer: consumer, From: from, To: from.AddDays(367)),
        (Consumer: consumer, From: from.ToOffset(TimeSpan.FromHours(1)), To: to)
    })
    {
        try
        {
            await validReader.ReadAsync(range.Consumer, range.From, range.To);
            throw new Exception("Bad period accepted");
        }
        catch (ArgumentException)
        {
            Check(transport.Calls == 0, "Bad period rejected");
        }
    }
});

Console.WriteLine($"PASS {groups} groups; {assertions} assertions. No network or operational data.");

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class CredentialProvider(ValidatedHumanUsageCredential? credential)
    : IValidatedHumanUsageCredentialProvider
{
    public ValueTask<ValidatedHumanUsageCredential?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(credential);
    }
}

sealed class FakeHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return send(request, cancellationToken);
    }
}

sealed class CountingStream(long length) : Stream
{
    private long remaining = length;
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var amount = (int)Math.Min(count, remaining);
        Array.Fill(buffer, (byte)' ', offset, amount);
        remaining -= amount;
        BytesRead += amount;
        return amount;
    }
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var amount = (int)Math.Min(buffer.Length, remaining);
        buffer.Span[..amount].Fill((byte)' ');
        remaining -= amount;
        BytesRead += amount;
        return ValueTask.FromResult(amount);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
