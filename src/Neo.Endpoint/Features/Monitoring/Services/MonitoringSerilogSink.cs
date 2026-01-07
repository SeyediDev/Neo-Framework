using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Neo.Endpoint.Features.Monitoring.Models;
using LogLevel = Neo.Endpoint.Features.Monitoring.Models.LogLevel;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Serilog sink that writes logs to ILogStore
/// </summary>
public class MonitoringSerilogSink : ILogEventSink
{
    private readonly ILogStore _store;
    private readonly LogEventLevel _minimumLevel;

    public MonitoringSerilogSink(ILogStore store, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        _store = store;
        _minimumLevel = minimumLevel;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < _minimumLevel) return;

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

        // Get current Activity if available
        var activity = Activity.Current;
        if (activity != null)
        {
            traceId ??= activity.TraceId.ToString();
            spanId ??= activity.SpanId.ToString();
        }

        // Format the base message
        var baseMessage = logEvent.RenderMessage();
        
        // Enhance message with exception details if exception exists
        var fullMessage = baseMessage;
        if (logEvent.Exception != null)
        {
            var exception = logEvent.Exception;
            var exceptionDetails = new System.Text.StringBuilder();
            exceptionDetails.AppendLine(baseMessage);
            exceptionDetails.AppendLine();
            exceptionDetails.AppendLine($"Exception Type: {exception.GetType().FullName}");
            exceptionDetails.AppendLine($"Exception Message: {exception.Message}");
            
            // Include inner exception if present
            var innerException = exception.InnerException;
            var depth = 0;
            while (innerException != null && depth < 5) // Limit depth to prevent infinite loops
            {
                exceptionDetails.AppendLine();
                exceptionDetails.AppendLine($"Inner Exception [{depth + 1}]:");
                exceptionDetails.AppendLine($"  Type: {innerException.GetType().FullName}");
                exceptionDetails.AppendLine($"  Message: {innerException.Message}");
                innerException = innerException.InnerException;
                depth++;
            }
            
            // Include stack trace if available
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                exceptionDetails.AppendLine();
                exceptionDetails.AppendLine("Stack Trace:");
                exceptionDetails.AppendLine(exception.StackTrace);
            }
            
            fullMessage = exceptionDetails.ToString();
        }

        var entry = new LogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = logEvent.Timestamp.UtcDateTime,
            Level = ConvertLogLevel(logEvent.Level),
            Message = fullMessage,
            MessageTemplate = logEvent.MessageTemplate.Text,
            SourceContext = sourceContext,
            TraceId = traceId,
            SpanId = spanId,
            RequestId = requestId,
            UserId = userId,
            MachineName = Environment.MachineName,
            ExceptionType = logEvent.Exception?.GetType().FullName,
            ExceptionMessage = logEvent.Exception?.Message,
            ExceptionStackTrace = logEvent.Exception?.StackTrace,
            Properties = properties
        };

        _store.Record(entry);
    }

    private static LogLevel ConvertLogLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => LogLevel.Trace,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            LogEventLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}

/// <summary>
/// Extension methods for adding monitoring sink to Serilog
/// </summary>
public static class MonitoringSerilogExtensions
{
    /// <summary>
    /// Add monitoring sink to Serilog configuration
    /// This should be called after the host is built when ILogStore is available
    /// </summary>
    public static LoggerConfiguration WriteTo_MonitoringStore(
        this LoggerSinkConfiguration sinkConfig,
        ILogStore store,
        LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        return sinkConfig.Sink(new MonitoringSerilogSink(store, minimumLevel));
    }
}

/// <summary>
/// Hosted service that adds MonitoringSerilogSink to Serilog at runtime
/// </summary>
public class SerilogMonitoringService : IHostedService
{
    private readonly ILogStore _store;
    private readonly IConfiguration _configuration;
    private Serilog.ILogger? _originalLogger;

    public SerilogMonitoringService(ILogStore store, IConfiguration configuration)
    {
        _store = store;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Save original logger
        _originalLogger = Log.Logger;

        // Create new logger with monitoring sink added
        var newLogger = new LoggerConfiguration()
            .ReadFrom.Configuration(_configuration)
            .WriteTo.Sink(new MonitoringSerilogSink(_store, LogEventLevel.Debug))
            .CreateLogger();

        // Replace the global logger
        Log.Logger = newLogger;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Restore original logger if needed
        if (_originalLogger != null)
        {
            Log.Logger = _originalLogger;
        }
        return Task.CompletedTask;
    }
}

