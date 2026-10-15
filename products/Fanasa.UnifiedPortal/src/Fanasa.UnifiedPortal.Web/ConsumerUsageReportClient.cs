using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Fanasa.UnifiedPortal.Web;

/// <summary>
/// Server-only seam for a validated HUMAN credential bound to the current session,
/// the approved Portal client and the central API audience/scope. A form subject or
/// client_credentials token is not a substitute. This client does not acquire tokens.
/// </summary>
public interface IConsumerUsageHumanCredentialProvider
{
    ValueTask<ConsumerUsageHumanCredential?> GetAsync(CancellationToken cancellationToken);
}

// Not a record: generated ToString/serialization must not expose the bearer token.
public sealed class ConsumerUsageHumanCredential(
    string authenticatedSubject, string bearerToken, DateTimeOffset expiresAt)
{
    internal string Subject { get; } = authenticatedSubject;
    internal string BearerToken { get; } = bearerToken;
    internal DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => nameof(ConsumerUsageHumanCredential) + " [redacted]";
}

public enum ConsumerUsageReadStatus { Success, Unauthorized, Forbidden, InvalidRequest, Unavailable }

// Safe presentation data only; no subject, raw response, bearer, exception or ledger evidence.
public sealed record ConsumerUsageReadResult(ConsumerUsageReadStatus Status, ConsumerUsageView View);

/// <summary>
/// One bounded GET to the configured central HTTPS origin. Uses the existing v1
/// projection rather than introducing a second financial parser. Not wired into
/// OIDC or Workspace until an approved human-credential provider is available.
/// </summary>
public sealed class ConsumerUsageReportClient : IDisposable
{
    private readonly Uri origin;
    private readonly IConsumerUsageHumanCredentialProvider credentials;
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;

    public ConsumerUsageReportClient(Uri centralOrigin, IConsumerUsageHumanCredentialProvider credentials,
        TimeSpan? timeout = null, TimeProvider? clock = null)
        : this(centralOrigin, credentials, CreateHandler(), timeout, clock) { }

    // Test seam only; the production constructor always owns the secure handler.
    internal ConsumerUsageReportClient(Uri centralOrigin, IConsumerUsageHumanCredentialProvider credentials,
        HttpMessageHandler handler, TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(centralOrigin);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(handler);
        if (!centralOrigin.IsAbsoluteUri || centralOrigin.Scheme != "https" || centralOrigin.Port != 443
            || centralOrigin.AbsolutePath != "/" || centralOrigin.UserInfo.Length != 0
            || centralOrigin.Query.Length != 0 || centralOrigin.Fragment.Length != 0)
            throw new ArgumentException("An exact HTTPS central origin on port 443 is required.", nameof(centralOrigin));
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        origin = centralOrigin;
        this.credentials = credentials;
        this.timeout = budget;
        this.clock = clock ?? TimeProvider.System;
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        PreAuthenticate = false, AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 32
    };

    public async Task<ConsumerUsageReadResult> ReadAsync(Guid consumer, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (consumer == Guid.Empty || from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero
            || from >= to || to - from >= TimeSpan.FromDays(366))
            throw new ArgumentException("A consumer and UTC period shorter than 366 days are required.");

        ConsumerUsageReadResult Failure(ConsumerUsageReadStatus status)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(status, ConsumerUsageReportProjection.Empty(consumer, from, to, "unavailable"));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var credential = await credentials.GetAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (credential is null || !Guid.TryParseExact(credential.Subject, "D", out var subject)
                || subject == Guid.Empty || subject.ToString("D") != credential.Subject
                || credential.ExpiresAt.Offset != TimeSpan.Zero || credential.ExpiresAt <= clock.GetUtcNow()
                || string.IsNullOrEmpty(credential.BearerToken) || credential.BearerToken.Length > 32768
                || credential.BearerToken.Any(c => c <= ' ' || c > '~'))
                return Failure(ConsumerUsageReadStatus.Unauthorized);

            var path = "/api/accounting/v1/consumers/" + consumer.ToString("D") + "/usage-report?from="
                + Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture)) + "&to="
                + Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture));
            var target = new Uri(origin, path);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => ConsumerUsageReadStatus.Unauthorized,
                    HttpStatusCode.Forbidden => ConsumerUsageReadStatus.Forbidden,
                    HttpStatusCode.BadRequest => ConsumerUsageReadStatus.InvalidRequest,
                    _ => ConsumerUsageReadStatus.Unavailable
                });

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if ((response.RequestMessage?.RequestUri is Uri actual && actual.AbsoluteUri != target.AbsoluteUri)
                || !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count != 0
                || response.Content.Headers.ContentLength > ConsumerUsageReportProjection.MaximumBytes
                || response.Headers.CacheControl?.NoStore != true)
                return Failure(ConsumerUsageReadStatus.Unavailable);

            using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bounded.Length + read > ConsumerUsageReportProjection.MaximumBytes)
                    return Failure(ConsumerUsageReadStatus.Unavailable);
                bounded.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            var view = ConsumerUsageReportProjection.Read(bounded.ToArray(), credential.Subject, consumer, from, to);
            deadline.Token.ThrowIfCancellationRequested();
            if (credential.ExpiresAt <= clock.GetUtcNow()) return Failure(ConsumerUsageReadStatus.Unauthorized);
            return new(view.Status == "unavailable" ? ConsumerUsageReadStatus.Unavailable : ConsumerUsageReadStatus.Success, view);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return Failure(ConsumerUsageReadStatus.Unavailable);
        }
    }

    public void Dispose() => client.Dispose();
}
