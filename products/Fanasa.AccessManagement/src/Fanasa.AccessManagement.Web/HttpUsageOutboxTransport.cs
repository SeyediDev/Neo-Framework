using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed class UsageOutboxOptions
{
    public bool Enabled { get; set; }
    public string DestinationId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public Guid ConsumerTenantId { get; set; }
    public string BearerToken { get; set; } = "";
    public int LeaseSeconds { get; set; } = 60;
    public int RetrySeconds { get; set; } = 30;
    public int PollSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 20;
    public int BatchSize { get; set; } = 100;

    public void Validate()
    {
        if (!Enabled) return;
        UsageEventV1.Label(DestinationId);
        if (DestinationId.Any(char.IsControl) || ConsumerTenantId == Guid.Empty
            || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || Endpoint.Length > 2000 || string.IsNullOrWhiteSpace(BearerToken) || BearerToken.Length > 8192
            || BearerToken.Any(c => c <= ' ' || c > '~') || LeaseSeconds is < 5 or > 300
            || TimeoutSeconds is < 1 or > 120 || LeaseSeconds < TimeoutSeconds + 5
            || RetrySeconds is < 1 or > 300 || PollSeconds is < 1 or > 3600 || BatchSize is < 1 or > 100)
            throw new ArgumentException("Enabled usage outbox requires a trusted HTTPS destination, server credential and bounded worker settings.");
    }
    public UsageOutboxSubscription Subscription()
    {
        Validate();
        if (!Enabled) throw new InvalidOperationException("Usage outbox delivery is disabled.");
        return new(DestinationId, new Uri(Endpoint).AbsoluteUri, ConsumerTenantId,
            TimeSpan.FromSeconds(LeaseSeconds), TimeSpan.FromSeconds(RetrySeconds));
    }
}

/// <summary>Sends the original event only to the configured destination. Credentials never enter Fabric.</summary>
public sealed class HttpUsageOutboxTransport(HttpClient client, UsageOutboxOptions options) : IUsageOutboxTransport
{
    public async Task<UsageOutboxAcknowledgement> SendAsync(UsageOutboxDelivery delivery, CancellationToken cancellationToken)
    {
        var subscription = options.Subscription();
        var message = UsageEventV1.FromJson(delivery.UsageJson);
        if (delivery.DestinationId != subscription.DestinationId || message.ConsumerTenantId != subscription.ConsumerTenantId
            || message.EventId != delivery.EventId || message.EventFingerprint != delivery.EventFingerprint
            || message.BusinessKey != delivery.LedgerKey)
            throw new InvalidDataException("Usage delivery does not match the configured destination.");
        UsageEventV1.Label(delivery.DeliveryId);
        if (delivery.DeliveryId.Any(char.IsControl)) throw new InvalidDataException("Invalid usage delivery identity.");
        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.EndpointBinding);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        request.Headers.Add("Idempotency-Key", delivery.DeliveryId);
        request.Content = new StringContent(delivery.UsageJson, Encoding.UTF8, "application/json");
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bodyTimeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Usage destination did not acknowledge delivery.");
        if (response.Content.Headers.ContentLength is > MaximumAcknowledgementBytes)
            throw new InvalidDataException("Usage acknowledgement exceeds its size limit.");
        // One bounded timeout covers response headers and the streamed acknowledgement.
        await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token);
        var bytes = new byte[MaximumAcknowledgementBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), bodyTimeout.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumAcknowledgementBytes) throw new InvalidDataException("Usage acknowledgement exceeds its size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid usage acknowledgement.");
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || !AcknowledgementFields.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid usage acknowledgement fields.");
            if (names.Count != AcknowledgementFields.Count) throw new InvalidDataException("Incomplete usage acknowledgement.");
            var ack = JsonSerializer.Deserialize<UsageOutboxAcknowledgement>(root.GetRawText(), AckOptions)
                ?? throw new InvalidDataException("Missing usage acknowledgement.");
            if (ack.DestinationId != delivery.DestinationId || ack.LedgerKey != delivery.LedgerKey || ack.EventId != delivery.EventId
                || ack.EventFingerprint != delivery.EventFingerprint || ack.Disposition is not ("accepted" or "duplicate"))
                throw new InvalidDataException("Usage acknowledgement identity does not match delivery.");
            return ack;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid usage acknowledgement JSON.", error); }
    }
    private const int MaximumAcknowledgementBytes = 16_384;
    private static readonly HashSet<string> AcknowledgementFields = new(StringComparer.Ordinal)
        { "destinationId", "ledgerKey", "eventId", "eventFingerprint", "disposition" };
    private static readonly JsonSerializerOptions AckOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true
    };
}

public sealed class UsageOutboxWorker(UsageOutboxDispatcher dispatcher, UsageOutboxOptions options,
    ILogger<UsageOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await dispatcher.DispatchBatchAsync(options.BatchSize, stoppingToken);
                if (result.ErrorCode is not null)
                    logger.LogWarning("Usage outbox requires attention: {Code}", result.ErrorCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            { logger.LogWarning("Usage outbox delivery stopped by durable state validation."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
