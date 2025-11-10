using Neo.Domain.Dto;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Neo.Infrastructure.Features.ServiceCaller;

public class LoggingHandler(ILogger<LoggingHandler> logger) 
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpLog log = new()
        {
            Method = request.Method.ToString(),
            Url = request.RequestUri?.ToString(),
            RequestHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
            RequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null
        };

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        stopwatch.Stop();

        log.StatusCode = (int)response.StatusCode;
        log.ResponseHeaders = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
        log.ResponseBody = response.Content != null ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
        log.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        // Log as structured JSON or any formatter
        logger.LogInformation("HTTP CLIENT Log: {@HttpLogRecord}", log);

        return response;
    }
}
