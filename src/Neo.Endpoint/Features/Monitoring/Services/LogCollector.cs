using Microsoft.Extensions.DependencyInjection;
using Neo.Endpoint.Features.Monitoring.Models;
using LogLevel = Neo.Endpoint.Features.Monitoring.Models.LogLevel;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Logger provider that captures logs for monitoring
/// </summary>
public class MonitoringLoggerProvider : ILoggerProvider
{
    private readonly ILogStore _store;
    private readonly ConcurrentDictionary<string, MonitoringLogger> _loggers = new();

    public MonitoringLoggerProvider(ILogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new MonitoringLogger(name, _store));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}

/// <summary>
/// Logger that captures logs for monitoring store
/// </summary>
public class MonitoringLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ILogStore _store;

    public MonitoringLogger(string categoryName, ILogStore store)
    {
        _categoryName = categoryName;
        _store = store;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel != Microsoft.Extensions.Logging.LogLevel.None;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var activity = Activity.Current;
        var properties = new Dictionary<string, object?>();

        // Extract properties from state if it's a structured log
        if (state is IEnumerable<KeyValuePair<string, object>> stateProperties)
        {
            foreach (var prop in stateProperties)
            {
                if (prop.Key != "{OriginalFormat}")
                {
                    properties[prop.Key] = prop.Value;
                }
            }
        }

        // Format the base message
        var baseMessage = formatter(state, exception);
        
        // Enhance message with exception details if exception exists
        var fullMessage = baseMessage;
        if (exception != null)
        {
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
            Timestamp = DateTime.UtcNow,
            Level = ConvertLogLevel(logLevel),
            Message = fullMessage,
            MessageTemplate = GetMessageTemplate(state),
            SourceContext = _categoryName,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            RequestId = activity?.GetBaggageItem("client.correlation.id"),
            UserId = activity?.GetBaggageItem("client.user.id"),
            MachineName = Environment.MachineName,
            ExceptionType = exception?.GetType().FullName,
            ExceptionMessage = exception?.Message,
            ExceptionStackTrace = exception?.StackTrace,
            Properties = properties
        };

        _store.Record(entry);
    }

    private static LogLevel ConvertLogLevel(Microsoft.Extensions.Logging.LogLevel level)
    {
        return level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => LogLevel.Trace,
            Microsoft.Extensions.Logging.LogLevel.Debug => LogLevel.Debug,
            Microsoft.Extensions.Logging.LogLevel.Information => LogLevel.Information,
            Microsoft.Extensions.Logging.LogLevel.Warning => LogLevel.Warning,
            Microsoft.Extensions.Logging.LogLevel.Error => LogLevel.Error,
            Microsoft.Extensions.Logging.LogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.None
        };
    }

    private static string? GetMessageTemplate<TState>(TState state)
    {
        if (state is IEnumerable<KeyValuePair<string, object>> stateProperties)
        {
            var template = stateProperties.FirstOrDefault(p => p.Key == "{OriginalFormat}");
            return template.Value?.ToString();
        }
        return null;
    }
}

/// <summary>
/// Extension methods for adding monitoring logger
/// </summary>
public static class MonitoringLoggerExtensions
{
    /// <summary>
    /// Add monitoring logger to logging builder
    /// </summary>
    public static ILoggingBuilder AddMonitoringLogger(this ILoggingBuilder builder)
    {
        builder.Services.AddSingleton<ILoggerProvider, MonitoringLoggerProvider>();
        return builder;
    }
}

/// <summary>
/// Hosted service that registers the MonitoringLoggerProvider with the ILoggerFactory
/// </summary>
public class LogCollectorHostedService : IHostedService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogStore _logStore;
    private MonitoringLoggerProvider? _provider;

    public LogCollectorHostedService(ILoggerFactory loggerFactory, ILogStore logStore)
    {
        _loggerFactory = loggerFactory;
        _logStore = logStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _provider = new MonitoringLoggerProvider(_logStore);
        _loggerFactory.AddProvider(_provider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _provider?.Dispose();
        return Task.CompletedTask;
    }
}

