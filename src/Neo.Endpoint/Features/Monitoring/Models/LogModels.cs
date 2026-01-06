namespace Neo.Endpoint.Features.Monitoring.Models;

/// <summary>
/// Represents a log entry
/// </summary>
public record LogEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }
    public string? MessageTemplate { get; init; }
    public string? SourceContext { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? RequestId { get; init; }
    public string? UserId { get; init; }
    public string? MachineName { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? ExceptionStackTrace { get; init; }
    public Dictionary<string, object?> Properties { get; init; } = [];
}

/// <summary>
/// Log levels (matches Microsoft.Extensions.Logging.LogLevel)
/// </summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6
}

/// <summary>
/// Log query request
/// </summary>
public record LogQueryRequest
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public LogLevel? MinLevel { get; init; }
    public LogLevel? MaxLevel { get; init; }
    public string? SourceContext { get; init; }
    public string? TraceId { get; init; }
    public string? MessageContains { get; init; }
    public string? UserId { get; init; }
    public bool? HasException { get; init; }
    public int? Limit { get; init; }
    public bool Descending { get; init; } = true;
}

/// <summary>
/// Log statistics
/// </summary>
public record LogStats
{
    public int TotalLogs { get; init; }
    public Dictionary<LogLevel, int> CountByLevel { get; init; } = [];
    public int ExceptionCount { get; init; }
    public DateTime? OldestEntry { get; init; }
    public DateTime? NewestEntry { get; init; }
    public Dictionary<string, int> TopSourceContexts { get; init; } = [];
    public Dictionary<string, int> TopExceptionTypes { get; init; } = [];
}

/// <summary>
/// Log timeline data for charts
/// </summary>
public record LogTimeline
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required int IntervalSeconds { get; init; }
    public List<LogTimelineBucket> Buckets { get; init; } = [];
}

/// <summary>
/// A bucket in the log timeline
/// </summary>
public record LogTimelineBucket
{
    public required DateTime Timestamp { get; init; }
    public int TotalCount { get; init; }
    public int TraceCount { get; init; }
    public int DebugCount { get; init; }
    public int InfoCount { get; init; }
    public int WarnCount { get; init; }
    public int ErrorCount { get; init; }
    public int CriticalCount { get; init; }
}

