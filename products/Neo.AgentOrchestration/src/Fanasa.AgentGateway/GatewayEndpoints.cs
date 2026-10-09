using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;

namespace Fanasa.AgentGateway;

public static class GatewayEndpoints
{
    public const int MaxBodyBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void MapGatewayEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapPost("/bindings/{key}/runs", async (string key, HttpContext context,
            ConfiguredGatewayBindings bindings, GatewayExecution execution, IHostEnvironment environment, IConfiguration config) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var loopbackTest = config.GetValue<bool>("AgentGateway:AllowLoopbackHttp") &&
                (environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
                context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
            if (!context.Request.IsHttps && !loopbackTest) return Error(400, "gateway-https-required");
            var auth = context.Request.Headers.Authorization;
            if (auth.Count != 1 || !AuthenticationHeaderValue.TryParse(auth[0], out var authorization) ||
                !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !bindings.Authenticate(key, authorization.Parameter)) return Error(401, "gateway-unauthorized");
            if (context.Request.Headers.ContentEncoding.Count != 0 ||
                !MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                contentType.CharSet is not null && !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                return Error(415, "gateway-content-type-invalid");
            var idempotency = context.Request.Headers["Idempotency-Key"];
            if (idempotency.Count != 1 || !Guid.TryParseExact(idempotency[0], "N", out var runId) || runId == Guid.Empty)
                return Error(400, "gateway-idempotency-invalid");
            if (context.Request.ContentLength > MaxBodyBytes) return Error(413, "gateway-request-too-large");
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var buffer = new MemoryStream(); var chunk = new byte[8192];
                for (int read; (read = await context.Request.Body.ReadAsync(chunk, timeout.Token)) != 0;)
                {
                    if (buffer.Length + read > MaxBodyBytes) return Error(413, "gateway-request-too-large");
                    buffer.Write(chunk, 0, read);
                }
                var run = await execution.ReserveAsync(key, Utf8.GetString(buffer.ToArray()), runId, timeout.Token);
                // Reservation + initial Outbox activation already committed.
                // No native prepare/submit, Hangfire call or task status write.
                return Results.Json(new { runId = run.RunId, phase = run.Phase.ToString() }, statusCode: 202);
            }
            catch (DecoderFallbackException) { return Error(400, "gateway-request-invalid"); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Error(413, "gateway-request-too-large"); }
            catch (GatewayConflictException ex) { return Error(409, ex.Code); }
            catch (ExternalAgentException) { return Error(400, "gateway-request-or-binding-invalid"); }
            catch (OperationCanceledException) { return Error(503, "gateway-reservation-unconfirmed"); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return Error(503, "gateway-reservation-unconfirmed"); }
        });
    }
    private static IResult Error(int status, string code) => Results.Json(new { code }, statusCode: status);
}
