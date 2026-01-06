namespace Neo.Endpoint.Features.Monitoring.Models;

/// <summary>
/// Represents a recorded metric data point
/// </summary>
public record MetricDataPoint
{
    public required string MetricName { get; init; }
    public required string MetricType { get; init; } // Counter, Gauge, Histogram, UpDownCounter
    public required DateTime Timestamp { get; init; }
    public required double Value { get; init; }
    public Dictionary<string, string> Tags { get; init; } = [];
    public string? Unit { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Represents a metric definition with its metadata
/// </summary>
public record MetricDefinition
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Unit { get; init; }
    public string? Description { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public HashSet<string> KnownTags { get; init; } = [];
}

/// <summary>
/// Aggregated metric statistics
/// </summary>
public record MetricStats
{
    public required string MetricName { get; init; }
    public required string MetricType { get; init; }
    public double CurrentValue { get; init; }
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
    public double AvgValue { get; init; }
    public double SumValue { get; init; }
    public int Count { get; init; }
    public DateTime FirstTimestamp { get; init; }
    public DateTime LastTimestamp { get; init; }
}

/// <summary>
/// Time series data for a metric
/// </summary>
public record MetricTimeSeries
{
    public required string MetricName { get; init; }
    public required string MetricType { get; init; }
    public string? Unit { get; init; }
    public List<TimeSeriesPoint> DataPoints { get; init; } = [];
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// A single point in a time series
/// </summary>
public record TimeSeriesPoint
{
    public required DateTime Timestamp { get; init; }
    public required double Value { get; init; }
}

/// <summary>
/// Request to query metrics
/// </summary>
public record MetricsQueryRequest
{
    public string? MetricName { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public Dictionary<string, string>? TagFilters { get; init; }
    public int? Limit { get; init; }
    public string? AggregationType { get; init; } // None, Avg, Sum, Min, Max, Count
    public int? AggregationIntervalSeconds { get; init; }
}

/// <summary>
/// Dashboard summary data
/// </summary>
public record MonitoringDashboardData
{
    public required DateTime GeneratedAt { get; init; }
    public required SystemMetrics System { get; init; }
    public required ApplicationMetrics Application { get; init; }
    public List<MetricStats> TopMetrics { get; init; } = [];
}

/// <summary>
/// System-level metrics
/// </summary>
public record SystemMetrics
{
    public double CpuUsagePercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public double MemoryUsagePercent { get; init; }
    public int ThreadCount { get; init; }
    public TimeSpan Uptime { get; init; }
    public int GcGen0Collections { get; init; }
    public int GcGen1Collections { get; init; }
    public int GcGen2Collections { get; init; }
    public long GcTotalMemory { get; init; }
}

/// <summary>
/// Application-level metrics
/// </summary>
public record ApplicationMetrics
{
    public long TotalRequests { get; init; }
    public long SuccessfulRequests { get; init; }
    public long FailedRequests { get; init; }
    public double SuccessRate { get; init; }
    public double AverageResponseTimeMs { get; init; }
    public int ActiveRequests { get; init; }
    public double RequestsPerSecond { get; init; }
}

