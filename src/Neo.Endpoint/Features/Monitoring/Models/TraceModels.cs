namespace Neo.Endpoint.Features.Monitoring.Models;

/// <summary>
/// Represents a recorded trace span
/// </summary>
public record TraceSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string OperationName { get; init; }
    public required string ServiceName { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public TimeSpan Duration => EndTime - StartTime;
    public required SpanStatus Status { get; init; }
    public string? StatusMessage { get; init; }
    public required SpanKind Kind { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
    public Dictionary<string, string> Baggage { get; init; } = [];
    public List<SpanEvent> Events { get; init; } = [];
}

/// <summary>
/// Span status
/// </summary>
public enum SpanStatus
{
    Unset = 0,
    Ok = 1,
    Error = 2
}

/// <summary>
/// Span kind (matches OpenTelemetry ActivityKind)
/// </summary>
public enum SpanKind
{
    Internal = 0,
    Server = 1,
    Client = 2,
    Producer = 3,
    Consumer = 4
}

/// <summary>
/// An event within a span
/// </summary>
public record SpanEvent
{
    public required string Name { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = [];
}

/// <summary>
/// A complete trace with all its spans
/// </summary>
public record TraceData
{
    public required string TraceId { get; init; }
    public required string RootOperationName { get; init; }
    public required string ServiceName { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime EndTime { get; init; }
    public TimeSpan Duration => EndTime - StartTime;
    public required SpanStatus Status { get; init; }
    public int SpanCount { get; init; }
    public int ErrorCount { get; init; }
    public List<TraceSpan> Spans { get; init; } = [];
}

/// <summary>
/// Trace query request
/// </summary>
public record TraceQueryRequest
{
    public string? TraceId { get; init; }
    public string? ServiceName { get; init; }
    public string? OperationName { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public TimeSpan? MinDuration { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public SpanStatus? Status { get; init; }
    public Dictionary<string, string>? TagFilters { get; init; }
    public int? Limit { get; init; }
    public string? OrderBy { get; init; } // StartTime, Duration, SpanCount
    public bool Descending { get; init; } = true;
}

/// <summary>
/// Trace statistics
/// </summary>
public record TraceStats
{
    public int TotalTraces { get; init; }
    public int TotalSpans { get; init; }
    public int ErrorTraces { get; init; }
    public double ErrorRate { get; init; }
    public double AvgDurationMs { get; init; }
    public double MinDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double P50DurationMs { get; init; }
    public double P90DurationMs { get; init; }
    public double P99DurationMs { get; init; }
    public Dictionary<string, int> ServiceCounts { get; init; } = [];
    public Dictionary<string, int> OperationCounts { get; init; } = [];
}

