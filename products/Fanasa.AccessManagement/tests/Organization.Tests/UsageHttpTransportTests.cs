using System.Net;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Metering;

static class UsageHttpTransportTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        _ = directory;
        var now = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero); var consumer = Guid.NewGuid();
        var message = new UsageEventV1("1.0", Guid.NewGuid(), now.AddMinutes(-1), now, new("synthetic-backend", "product-backend", "synthetic-host"),
            "development", Guid.NewGuid(), consumer, "organization-fabric", "test-contract", 7, "test-allocation", "2026-10",
            new("http-operation", "organization.unit.save/v1", "synthetic-member", Guid.NewGuid(), UsageEventV1.Hash("request"), now.AddMinutes(-2), "organization-unit-committed/v1"),
            new("decision", "organization-permission/v1", "credit"), new("organization.change", "1", "change", "1"),
            new("test-rate", 7, now.AddDays(-1), now.AddDays(1), "12", "IRR", 0, "away-from-zero", "postpaid"),
            "completed", new("receipt", UsageEventV1.Hash("result"), "fabric://synthetic/receipt"), Guid.NewGuid());
        var options = Options(consumer); var capture = new Capture(); using var client = new HttpClient(capture);
        var transport = new HttpUsageOutboxTransport(client, options);
        var delivery = new UsageOutboxDelivery(options.DestinationId, message.BusinessKey, message.EventId, message.EventFingerprint,
            "synthetic-delivery-identity", message.ToWireJson(), 42, "synthetic-source-database");
        var acknowledgement = new UsageOutboxAcknowledgement(delivery.DestinationId, delivery.LedgerKey, delivery.EventId, delivery.EventFingerprint, "accepted");
        capture.ResponseBody = Ack(acknowledgement);
        var result = Send(transport, delivery);
        check(result == acknowledgement && capture.Method == HttpMethod.Post && capture.Endpoint == options.Endpoint
            && capture.Body == message.ToWireJson() && capture.MediaType == "application/json",
            "usage HTTP transport posts exact original wire event to configured destination");
        check(capture.Authorization == "Bearer synthetic-server-credential" && capture.IdempotencyKey == delivery.DeliveryId,
            "usage HTTP transport binds server credential and stable idempotency header");
        capture.ResponseBody = Ack(acknowledgement with { Disposition = "duplicate" });
        check(Send(transport, delivery).Disposition == "duplicate" && capture.Body == delivery.UsageJson,
            "usage HTTP duplicate receipt acknowledges unchanged event bytes");
        foreach (var (changed, label) in new (UsageOutboxAcknowledgement Ack, string Label)[]
        {
            (acknowledgement with { DestinationId = "other-destination" }, "destination"),
            (acknowledgement with { LedgerKey = "other-ledger" }, "ledger key"),
            (acknowledgement with { EventId = Guid.NewGuid() }, "event ID"),
            (acknowledgement with { EventFingerprint = UsageEventV1.Hash("other") }, "fingerprint"),
            (acknowledgement with { Disposition = "pending" }, "disposition")
        })
        {
            capture.ResponseBody = Ack(changed);
            Reject<InvalidDataException>(() => Send(transport, delivery), check, "usage HTTP rejects acknowledgement with mismatched " + label);
        }
        var validWire = Ack(acknowledgement);
        foreach (var (wire, label) in new (string Wire, string Label)[]
        {
            (validWire.Insert(1, "\"extra\":\"x\","), "unknown field"),
            (validWire.Replace("\"disposition\":\"accepted\"", "\"disposition\":\"accepted\",\"disposition\":\"accepted\"", StringComparison.Ordinal), "duplicate field"),
            (validWire.Replace(",\"disposition\":\"accepted\"", "", StringComparison.Ordinal), "missing field"),
            (validWire.Replace("\"disposition\":\"accepted\"", "\"disposition\":null", StringComparison.Ordinal), "null field"),
            (validWire.Replace("\"disposition\":\"accepted\"", "\"disposition\":1", StringComparison.Ordinal), "numeric field"),
            ("[]", "non-object payload"),
            ("{not-json", "malformed JSON")
        })
        {
            capture.ResponseBody = wire;
            Reject<InvalidDataException>(() => Send(transport, delivery), check, "usage HTTP strict acknowledgement rejects " + label);
        }
        capture.ResponseBody = new string(' ', 16_385); capture.UnknownLength = false;
        Reject<InvalidDataException>(() => Send(transport, delivery), check, "usage HTTP rejects oversized known-length acknowledgement before parsing");
        capture.UnknownLength = true;
        Reject<InvalidDataException>(() => Send(transport, delivery), check, "usage HTTP bounds streamed acknowledgement without content length");
        capture.UnknownLength = false; capture.ResponseBody = validWire; capture.Status = HttpStatusCode.ServiceUnavailable;
        Reject<HttpRequestException>(() => Send(transport, delivery), check, "usage HTTP server failure cannot acknowledge delivery");
        capture.Status = HttpStatusCode.TemporaryRedirect;
        Reject<HttpRequestException>(() => Send(transport, delivery), check, "usage HTTP redirect response cannot acknowledge delivery");
        capture.Status = HttpStatusCode.OK;
        var calls = capture.Calls;
        Reject<InvalidDataException>(() => Send(transport, delivery with { DestinationId = "wrong-destination" }), check,
            "usage HTTP wrong destination is rejected before transport");
        Reject<InvalidDataException>(() => Send(transport, delivery with { EventFingerprint = UsageEventV1.Hash("wrong-event") }), check,
            "usage HTTP mutated event identity is rejected before transport");
        Reject<InvalidDataException>(() => Send(transport, delivery with { DeliveryId = "bad\r\nidentity" }), check,
            "usage HTTP idempotency header cannot contain control characters");
        var wrongScope = Options(Guid.NewGuid()); var scopedTransport = new HttpUsageOutboxTransport(client, wrongScope);
        Reject<InvalidDataException>(() => Send(scopedTransport, delivery), check,
            "usage HTTP consumer scope cannot send another tenant complete event");
        check(capture.Calls == calls, "usage HTTP destination and scope rejection sends no network request");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => transport.SendAsync(delivery, cancelled.Token).GetAwaiter().GetResult(), check,
            "usage HTTP cancellation propagates without accepted receipt");
        var disabled = new UsageOutboxOptions(); disabled.Validate();
        Reject<InvalidOperationException>(() => disabled.Subscription(), check, "usage outbox disabled configuration cannot create a worker subscription");
        foreach (var endpoint in new[] { "http://usage.invalid/receive", "https://user:password@usage.invalid/receive", "https://usage.invalid/receive?token=x", "https://usage.invalid/receive#fragment" })
        {
            var invalid = Options(consumer); invalid.Endpoint = endpoint;
            Reject<ArgumentException>(invalid.Validate, check, "usage outbox endpoint rejects unsafe binding " + endpoint.Split(':')[0]);
        }
        var shortLease = Options(consumer); shortLease.LeaseSeconds = shortLease.TimeoutSeconds + 4;
        Reject<ArgumentException>(shortLease.Validate, check, "usage outbox lease must cover transport timeout plus safety interval");
        var badToken = Options(consumer); badToken.BearerToken = "synthetic token with spaces";
        Reject<ArgumentException>(badToken.Validate, check, "usage outbox rejects credentials unsafe for HTTP authorization headers");
        var batch = Options(consumer); batch.BatchSize = 101;
        Reject<ArgumentException>(batch.Validate, check, "usage outbox batch size respects bounded Fabric polling");
    }

    private static UsageOutboxOptions Options(Guid consumer) => new()
    {
        Enabled = true, DestinationId = "synthetic-usage-destination", Endpoint = "https://usage.invalid/receive", ConsumerTenantId = consumer,
        BearerToken = "synthetic-server-credential", LeaseSeconds = 60, RetrySeconds = 10, TimeoutSeconds = 20
    };
    private static string Ack(UsageOutboxAcknowledgement acknowledgement) => JsonSerializer.Serialize(acknowledgement,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static UsageOutboxAcknowledgement Send(HttpUsageOutboxTransport transport, UsageOutboxDelivery delivery)
        => transport.SendAsync(delivery, default).GetAwaiter().GetResult();
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private sealed class Capture : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Endpoint { get; private set; }
        public string? Body { get; private set; }
        public string? MediaType { get; private set; }
        public string? Authorization { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string ResponseBody { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool UnknownLength { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Method = request.Method; Endpoint = request.RequestUri!.AbsoluteUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken); MediaType = request.Content.Headers.ContentType?.MediaType;
            Authorization = request.Headers.Authorization?.ToString(); IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return new(Status) { Content = UnknownLength ? new UnknownLengthContent(ResponseBody) : new StringContent(ResponseBody, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class UnknownLengthContent(string value) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
    }
}
