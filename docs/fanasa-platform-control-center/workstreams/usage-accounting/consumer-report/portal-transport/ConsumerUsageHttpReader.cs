using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fanasa.UsageAccounting.PortalTransport;

/// <summary>
/// Implement in the Portal server only. Return a validated HUMAN credential for the central
/// API audience, bound to the current authenticated session and an independently trusted Portal
/// client. Form/header subjects and client_credentials tokens do not satisfy this contract.
/// Null means no credential exists. This reader never provisions or exchanges tokens.
/// </summary>
public interface IValidatedHumanUsageCredentialProvider
{
    ValueTask<ValidatedHumanUsageCredential?> GetAsync(CancellationToken cancellationToken);
}

// A class avoids a positional record's implicit bearer-token disclosure through ToString.
public sealed class ValidatedHumanUsageCredential(
    string authenticatedSubject, string bearerToken, DateTimeOffset expiresAt)
{
    public string AuthenticatedSubject { get; } = authenticatedSubject;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    internal string BearerToken { get; } = bearerToken;
    public override string ToString() => nameof(ValidatedHumanUsageCredential) + " [redacted]";
}

// Server-side transport DTOs. Map before browser serialization; Subject is internal binding data.
// No central Web assembly or product-name lookup is required here.
public sealed record ConsumerUsageTransportEnvelope(
    string Subject, ConsumerUsageTransportReport Report);

public sealed record ConsumerUsageTransportReport(
    Guid ConsumerTenantId, DateTimeOffset From, DateTimeOffset To, DateTimeOffset? AsOf,
    string Status, string? Currency, string? TotalAmount,
    IReadOnlyList<ConsumerUsageTransportItem> Items, string Basis = "posted-ledger");

public sealed record ConsumerUsageTransportItem(
    string ProductKey, string MeterId, string? MeterVersion, string? Unit,
    string? Quantity, string Amount, int ChargeCount);

public enum ConsumerUsageTransportStatus
{
    Success, Unauthorized, Forbidden, InvalidRequest, Unavailable
}

public sealed record ConsumerUsageReadResult(
    ConsumerUsageTransportStatus TransportStatus, ConsumerUsageTransportEnvelope Envelope);

/// <summary>
/// One bounded read against one configured HTTPS origin. No redirects, cookies, token refresh,
/// authorization retries or service-client fallback. Does not receive or modify service lists.
/// </summary>
public sealed class ConsumerUsageHttpReader : IDisposable
{
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumItems = 10000;

    private static readonly Regex PlainDecimal = new(
        @"^(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex UtcText = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly Uri origin;
    private readonly IValidatedHumanUsageCredentialProvider credentials;
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;

    public ConsumerUsageHttpReader(
        Uri centralOrigin, IValidatedHumanUsageCredentialProvider credentials,
        TimeSpan? timeout = null, TimeProvider? clock = null)
        : this(centralOrigin, credentials, CreateSecureHandler(), timeout, clock)
    {
    }

    // Only the self-contained tests use this injection seam.
    internal ConsumerUsageHttpReader(
        Uri centralOrigin, IValidatedHumanUsageCredentialProvider credentials,
        HttpMessageHandler handler, TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(centralOrigin);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(handler);

        if (!centralOrigin.IsAbsoluteUri || centralOrigin.Scheme != "https"
            || centralOrigin.Port != 443 || centralOrigin.AbsolutePath != "/"
            || centralOrigin.UserInfo.Length != 0 || centralOrigin.Query.Length != 0
            || centralOrigin.Fragment.Length != 0)
            throw new ArgumentException(
                "An exact HTTPS central origin on port 443 is required.", nameof(centralOrigin));

        var budget = timeout ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        origin = centralOrigin;
        this.credentials = credentials;
        this.timeout = budget;
        this.clock = clock ?? TimeProvider.System;
        client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateSecureHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 32
    };

    public async Task<ConsumerUsageReadResult> ReadAsync(
        Guid consumerTenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (consumerTenantId == Guid.Empty || from.Offset != TimeSpan.Zero
            || to.Offset != TimeSpan.Zero || from >= to
            || to - from >= TimeSpan.FromDays(366))
            throw new ArgumentException(
                "A consumer and UTC period shorter than 366 days is required.");

        var subject = "";
        ConsumerUsageReadResult Failure(ConsumerUsageTransportStatus status)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(status, new(subject,
                new(consumerTenantId, from, to, null, "unavailable", null, null, [])));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            var credential = await credentials.GetAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();

            if (credential is null || !Label(credential.AuthenticatedSubject)
                || credential.ExpiresAt.Offset != TimeSpan.Zero
                || credential.ExpiresAt <= clock.GetUtcNow()
                || string.IsNullOrWhiteSpace(credential.BearerToken)
                || credential.BearerToken.Length > 32768
                || credential.BearerToken.Any(char.IsWhiteSpace)
                || credential.BearerToken.Any(char.IsControl))
                return Failure(ConsumerUsageTransportStatus.Unauthorized);

            subject = credential.AuthenticatedSubject;
            var path = "/api/accounting/v1/consumers/" + consumerTenantId.ToString("D")
                + "/usage-report?from="
                + Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))
                + "&to=" + Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture));
            var target = new Uri(origin, path);

            using var request = new HttpRequestMessage(HttpMethod.Get, target)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credential.BearerToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoStore = true, NoCache = true
            };

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();

            // Exactly one SendAsync; redirects and authorization failures do not resend tokens.
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => ConsumerUsageTransportStatus.Unauthorized,
                    HttpStatusCode.Forbidden => ConsumerUsageTransportStatus.Forbidden,
                    HttpStatusCode.BadRequest => ConsumerUsageTransportStatus.InvalidRequest,
                    _ => ConsumerUsageTransportStatus.Unavailable
                });

            if (response.RequestMessage?.RequestUri is Uri actual
                && actual.AbsoluteUri != target.AbsoluteUri)
                return Failure(ConsumerUsageTransportStatus.Unavailable);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if ((mediaType != "application/json"
                    && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
                || response.Content.Headers.ContentEncoding.Count != 0
                || response.Content.Headers.ContentLength > MaximumResponseBytes
                || response.Headers.CacheControl?.NoStore != true)
                return Failure(ConsumerUsageTransportStatus.Unavailable);

            using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bounded.Length + read > MaximumResponseBytes)
                    return Failure(ConsumerUsageTransportStatus.Unavailable);
                bounded.Write(buffer, 0, read);
            }

            deadline.Token.ThrowIfCancellationRequested();
            var envelope = Parse(bounded.ToArray(), subject, consumerTenantId, from, to);
            deadline.Token.ThrowIfCancellationRequested();

            if (credential.ExpiresAt <= clock.GetUtcNow())
                return Failure(ConsumerUsageTransportStatus.Unauthorized);

            return new(ConsumerUsageTransportStatus.Success, envelope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or JsonException or InvalidDataException or FormatException or OverflowException
            or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(ConsumerUsageTransportStatus.Unavailable);
        }
    }

    private static ConsumerUsageTransportEnvelope Parse(
        byte[] bytes, string subject, Guid consumer, DateTimeOffset from, DateTimeOffset to)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = Shape(document.RootElement, "subject", "report");
        if (Text(root["subject"]) != subject) throw new InvalidDataException();

        var report = Shape(root["report"], "consumerTenantId", "from", "to", "asOf",
            "status", "currency", "totalAmount", "items", "basis");

        if (!Guid.TryParseExact(Text(report["consumerTenantId"]), "D", out var actualConsumer)
            || actualConsumer != consumer || Utc(report["from"]) != from
            || Utc(report["to"]) != to || Text(report["basis"]) != "posted-ledger")
            throw new InvalidDataException();

        var state = Text(report["status"]);
        if (state is not ("ready" or "notRecorded" or "incomplete" or "unavailable"))
            throw new InvalidDataException();

        var asOf = report["asOf"].ValueKind == JsonValueKind.Null
            ? (DateTimeOffset?)null : Utc(report["asOf"]);
        var currency = NullableText(report["currency"]);
        var totalAmount = NullableText(report["totalAmount"]);

        if (report["items"].ValueKind != JsonValueKind.Array
            || report["items"].GetArrayLength() > MaximumItems)
            throw new InvalidDataException();

        if (state != "ready")
        {
            if (currency is not null || totalAmount is not null
                || report["items"].GetArrayLength() != 0)
                throw new InvalidDataException();

            // Server may retain asOf in a failed report; financial presentation must clear it.
            return new(subject, new(consumer, from, to, null, state, null, null, []));
        }

        if (asOf is null) throw new InvalidDataException();
        var minorScale = currency switch
        {
            "IRR" => 0, "USD" or "EUR" => 2, _ => throw new InvalidDataException()
        };
        var total = MoneyUnits(totalAmount, minorScale);
        var items = new List<ConsumerUsageTransportItem>();
        var groups = new HashSet<(string Product, string Meter, string? Version, string? Unit)>();
        BigInteger sum = 0;
        var charges = 0;

        foreach (var entry in report["items"].EnumerateArray())
        {
            var row = Shape(entry, "productKey", "meterId", "meterVersion", "unit",
                "quantity", "amount", "chargeCount");
            var product = Text(row["productKey"]);
            var meter = Text(row["meterId"]);
            var version = NullableText(row["meterVersion"]);
            var unit = NullableText(row["unit"]);
            var quantity = NullableText(row["quantity"]);
            var amount = Text(row["amount"]);

            if (!Label(product) || !Label(meter)
                || row["chargeCount"].ValueKind != JsonValueKind.Number
                || !row["chargeCount"].TryGetInt32(out var count)
                || count is < 1 or > MaximumItems
                || !groups.Add((product, meter, version, unit)))
                throw new InvalidDataException();

            var legacy = version is null && unit is null && quantity is null;
            if (!legacy && (!Label(version) || !Label(unit) || ExactDecimal(quantity) <= 0))
                throw new InvalidDataException();

            charges = checked(charges + count);
            if (charges > MaximumItems) throw new InvalidDataException();
            sum += MoneyUnits(amount, minorScale);
            items.Add(new(product, meter, version, unit, quantity, amount, count));
        }

        if (sum != total) throw new InvalidDataException();
        return new(subject, new(consumer, from, to, asOf, state, currency, totalAmount,
            items.AsReadOnly()));
    }

    private static Dictionary<string, JsonElement> Shape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Contains(property.Name, StringComparer.Ordinal)
                || !values.TryAdd(property.Name, property.Value))
                throw new InvalidDataException();
        if (values.Count != fields.Length) throw new InvalidDataException();
        return values;
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException();

    private static string? NullableText(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value);

    private static DateTimeOffset Utc(JsonElement value)
    {
        var text = Text(value);
        if (!UtcText.IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException();
        return parsed;
    }

    private static bool Label(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);

    private static decimal ExactDecimal(string? text)
    {
        if (text is null || text.Length > 64 || !PlainDecimal.IsMatch(text)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException();

        var dot = text.IndexOf('.');
        var sourceScale = dot < 0 ? 0 : text.Length - dot - 1;
        var sourceCoefficient = BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture);
        var (coefficient, scale) = Parts(value);
        if (sourceCoefficient * BigInteger.Pow(10, scale)
            != coefficient * BigInteger.Pow(10, sourceScale))
            throw new InvalidDataException();
        return value;
    }

    private static BigInteger MoneyUnits(string? text, int minorScale)
    {
        var (coefficient, scale) = Parts(ExactDecimal(text));
        while (scale > minorScale && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
        if (scale > minorScale) throw new InvalidDataException();
        return coefficient * BigInteger.Pow(10, minorScale - scale);
    }

    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        return ((BigInteger)(uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64),
            (bits[3] >> 16) & 255);
    }

    public void Dispose() => client.Dispose();
}
