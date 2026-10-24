using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

/// <summary>Authenticated entry to the isolated laboratory, never an authority for product access.</summary>
[ApiController]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
[Route("api/workbench")]
public sealed class WorkbenchApi : ControllerBase
{
    public const int MaximumRequestBytes = 256 * 1024;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly Uri Upstream = new("http://127.0.0.1:4187/api/workbench");
    private static readonly HttpClient ProxyClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal) { "", "export", "history", "readiness" };
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal)
        { "draft", "publish", "retire", "bind", "evaluate", "execute", "reset", "validate" };
    private readonly ILogger<WorkbenchApi> logger;
    private readonly HttpClient client;

    public WorkbenchApi(ILogger<WorkbenchApi> logger) : this(logger, ProxyClient) { }

    // A local HTTP test can supply a transport without opening or stopping the actual laboratory.
    internal WorkbenchApi(ILogger<WorkbenchApi> logger, HttpClient client)
    {
        this.logger = logger;
        this.client = client;
    }

    [HttpGet("{operation?}")]
    public Task<IActionResult> Read(string? operation, CancellationToken cancellationToken) =>
        Forward(operation ?? "", false, cancellationToken);

    [HttpPost("{operation}")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaximumRequestBytes)]
    public Task<IActionResult> Write(string operation, CancellationToken cancellationToken) =>
        Forward(operation, true, cancellationToken);

    private async Task<IActionResult> Forward(string operation, bool write, CancellationToken cancellationToken)
    {
        if (!(write ? Writes : Reads).Contains(operation) || Request.QueryString.HasValue)
            return BadRequest(new { error = "unsupported-workbench-operation" });

        using var request = new HttpRequestMessage(write ? HttpMethod.Post : HttpMethod.Get,
            operation.Length == 0 ? Upstream : new Uri(Upstream.AbsoluteUri + "/" + operation));
        // Native lab origin is fixed. Authentication, cookies and arbitrary caller headers never cross this seam.
        request.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:4186");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (write)
        {
            if (Request.ContentLength is > MaximumRequestBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "workbench-request-too-large" });
            if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
                || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { error = "json-required" });
            byte[]? body;
            try { body = await ReadBounded(Request.Body, MaximumRequestBytes, cancellationToken); }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            { return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "workbench-request-too-large" }); }
            if (body is null) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "workbench-request-too-large" });
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            logger.LogInformation("Laboratory operation {Operation} requested by authenticated operator {Operator}; author/reviewer fields are laboratory identities.",
                operation, User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown");
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var body = await ReadBounded(stream, MaximumResponseBytes, cancellationToken);
            if (body is null) return Unavailable();
            Response.StatusCode = (int)response.StatusCode;
            Response.Headers.CacheControl = "no-store";
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers["X-Workbench-Data-Mode"] = "isolated-laboratory";
            return File(body, "application/json; charset=utf-8");
        }
        catch (HttpRequestException) { return Unavailable(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
    }

    private ObjectResult Unavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable,
        new { error = "workbench-unavailable", message = "پاسخ موتور کارگاه دریافت نشد؛ پیش از تلاش دوباره وضعیت را تازه‌سازی کنید." });

    private static async Task<byte[]?> ReadBounded(Stream source, int maximumBytes, CancellationToken cancellationToken)
    {
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) return target.ToArray();
            if (target.Length + count > maximumBytes) return null;
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
