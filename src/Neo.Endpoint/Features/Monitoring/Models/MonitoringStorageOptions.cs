namespace Neo.Endpoint.Features.Monitoring.Models;

/// <summary>
/// Configuration options for Monitoring storage
/// </summary>
public class MonitoringStorageOptions
{
    /// <summary>
    /// Maximum retention time for metrics in memory (default: 1 hour)
    /// </summary>
    public TimeSpan MetricsRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum retention time for traces in memory (default: 1 hour)
    /// </summary>
    public TimeSpan TracesRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum retention time for logs in memory (default: 1 hour)
    /// </summary>
    public TimeSpan LogsRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of metric data points to keep per metric
    /// </summary>
    public int MaxMetricDataPoints { get; set; } = 3600; // 1 per second for 1 hour

    /// <summary>
    /// Maximum number of traces to keep
    /// </summary>
    public int MaxTraces { get; set; } = 10000;

    /// <summary>
    /// Maximum number of log entries to keep
    /// </summary>
    public int MaxLogEntries { get; set; } = 10000;

    /// <summary>
    /// Metrics collection interval in milliseconds
    /// </summary>
    public int MetricsCollectionIntervalMs { get; set; } = 10000;
}

