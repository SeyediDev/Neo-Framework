using System.Net;

namespace Fanasa.UnifiedPortal.Web;

public sealed record ConsumerAccountReadResult(ConsumerUsageReadStatus Status, ConsumerAccountView View);

// Response validation only: this type sends no request and acquires no credential.
public static class ConsumerAccountHttpResponse
{
    public static async Task<ConsumerAccountReadResult> ReadAsync(HttpResponseMessage response, Uri expectedTarget,
        string expectedSubject, Guid consumer, CancellationToken cancellationToken = default)
    {
        ConsumerAccountReadResult Failure(ConsumerUsageReadStatus status) => new(status, ConsumerAccountProjection.Empty(consumer));
        cancellationToken.ThrowIfCancellationRequested();
        if (response.StatusCode != HttpStatusCode.OK)
            return Failure(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ConsumerUsageReadStatus.Unauthorized,
                HttpStatusCode.Forbidden => ConsumerUsageReadStatus.Forbidden,
                HttpStatusCode.BadRequest => ConsumerUsageReadStatus.InvalidRequest,
                _ => ConsumerUsageReadStatus.Unavailable
            });
        if (response.RequestMessage?.RequestUri is { } actual && actual.AbsoluteUri != expectedTarget.AbsoluteUri
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentEncoding.Count != 0 || response.Headers.CacheControl?.NoStore != true
            || response.Content.Headers.ContentLength > ConsumerAccountProjection.MaximumBytes)
            return Failure(ConsumerUsageReadStatus.Unavailable);
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bounded = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (bounded.Length + read > ConsumerAccountProjection.MaximumBytes) return Failure(ConsumerUsageReadStatus.Unavailable);
                bounded.Write(buffer, 0, read);
            }
            var view = ConsumerAccountProjection.Read(bounded.ToArray(), expectedSubject, consumer);
            cancellationToken.ThrowIfCancellationRequested();
            return new(view.Status == "unavailable" ? ConsumerUsageReadStatus.Unavailable : ConsumerUsageReadStatus.Success, view);
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        { return Failure(ConsumerUsageReadStatus.Unavailable); }
    }
}
