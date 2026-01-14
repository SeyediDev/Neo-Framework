using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Text;
using System.Text.Json;

namespace Neo.Infrastructure.Features.Telementry;

/// <summary>
/// Serilog sink that sends logs to the monitoring API endpoint
/// هر API لاگ‌های خودش را به خودش ارسال می‌کند (self-monitoring)
/// </summary>
public class NeoMonitoringSerilogSink(
    string monitoringApiUrl,
    HttpClient? httpClient = null,
    LogEventLevel minimumLevel = LogEventLevel.Information,
    ILogger? logger = null) : ILogEventSink
{
    private readonly string _monitoringApiUrl = monitoringApiUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(monitoringApiUrl));
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < minimumLevel) return;

        try
        {
            var logEntry = ConvertToLogEntry(logEvent);
            var json = JsonSerializer.Serialize(logEntry, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Send asynchronously without waiting (fire and forget)
            _ = _httpClient.PostAsync($"{_monitoringApiUrl}/api/monitoring/logs/otlp", content)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        logger?.Warning(task.Exception, "Failed to send log to monitoring API");
                    }
                });
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Error sending log to monitoring API");
        }
    }

    private static object ConvertToLogEntry(LogEvent logEvent)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var prop in logEvent.Properties)
        {
            properties[prop.Key] = prop.Value.ToString();
        }

        // Extract common properties
        var sourceContext = logEvent.Properties.TryGetValue("SourceContext", out var sc)
            ? sc.ToString().Trim('"')
            : null;
        var requestId = logEvent.Properties.TryGetValue("RequestId", out var ri)
            ? ri.ToString().Trim('"')
            : null;
        var traceId = logEvent.Properties.TryGetValue("TraceId", out var ti)
            ? ti.ToString().Trim('"')
            : null;
        var spanId = logEvent.Properties.TryGetValue("SpanId", out var si)
            ? si.ToString().Trim('"')
            : null;
        var userId = logEvent.Properties.TryGetValue("UserId", out var ui)
            ? ui.ToString().Trim('"')
            : null;
		var corelationId = logEvent.Properties.TryGetValue("CirelationId", out var ci)
			? ci.ToString().Trim('"')
			: null;

		// Get current Activity if available
		var activity = System.Diagnostics.Activity.Current;
        if (activity != null)
        {
            traceId ??= activity.TraceId.ToString();
            spanId ??= activity.SpanId.ToString();
        }

        return new
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = logEvent.Timestamp.UtcDateTime,
            Level = ConvertLogLevel(logEvent.Level),
            Message = logEvent.RenderMessage(),
            MessageTemplate = logEvent.MessageTemplate.Text,
            SourceContext = sourceContext,
            TraceId = traceId,
            SpanId = spanId,
            RequestId = requestId,
            UserId = userId,
			CorelationId = corelationId,
			MachineName = Environment.MachineName,
            ExceptionType = logEvent.Exception?.GetType().FullName,
            ExceptionMessage = logEvent.Exception?.Message,
            ExceptionStackTrace = logEvent.Exception?.StackTrace,
            Properties = properties
        };
    }

    private static string ConvertLogLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "Trace",
            LogEventLevel.Debug => "Debug",
            LogEventLevel.Information => "Information",
            LogEventLevel.Warning => "Warning",
            LogEventLevel.Error => "Error",
            LogEventLevel.Fatal => "Critical",
            _ => "Information"
        };
    }
}