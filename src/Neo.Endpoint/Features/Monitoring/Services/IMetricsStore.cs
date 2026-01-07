using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Interface for in-memory metrics storage
/// </summary>
public interface IMetricsStore
{
    /// <summary>
    /// Record a metric data point
    /// </summary>
    void Record(MetricDataPoint dataPoint);

    /// <summary>
    /// Get all metric definitions
    /// </summary>
    IEnumerable<MetricDefinition> GetMetricDefinitions();

    /// <summary>
    /// Get metric definition by name
    /// </summary>
    MetricDefinition? GetMetricDefinition(string metricName);

    /// <summary>
    /// Query metric data points
    /// </summary>
    IEnumerable<MetricDataPoint> Query(MetricsQueryRequest request);

    /// <summary>
    /// Get time series data for a metric
    /// </summary>
    MetricTimeSeries GetTimeSeries(string metricName, DateTime? from = null, DateTime? to = null, 
        Dictionary<string, string>? tagFilters = null, int? aggregationIntervalSeconds = null);

    /// <summary>
    /// Get statistics for a metric
    /// </summary>
    MetricStats? GetStats(string metricName, DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Get current system metrics
    /// </summary>
    SystemMetrics GetSystemMetrics();

    /// <summary>
    /// Get application metrics
    /// </summary>
    ApplicationMetrics GetApplicationMetrics();

    /// <summary>
    /// Get dashboard summary
    /// </summary>
    MonitoringDashboardData GetDashboardData();

    /// <summary>
    /// Clear old data based on retention policy
    /// </summary>
    void Cleanup();

    /// <summary>
    /// Reset all metrics (clear all data points and statistics)
    /// </summary>
    void Reset();
}

