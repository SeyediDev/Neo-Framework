using Neo.Endpoint.Features.Monitoring.Models;
using LogLevel = Neo.Endpoint.Features.Monitoring.Models.LogLevel;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Interface for in-memory log storage
/// </summary>
public interface ILogStore
{
    /// <summary>
    /// Record a log entry
    /// </summary>
    void Record(LogEntry entry);

    /// <summary>
    /// Query log entries
    /// </summary>
    IEnumerable<LogEntry> Query(LogQueryRequest request);

    /// <summary>
    /// Get recent log entries
    /// </summary>
    IEnumerable<LogEntry> GetRecentLogs(int limit = 100, LogLevel? minLevel = null);

    /// <summary>
    /// Get log statistics
    /// </summary>
    LogStats GetStats(DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Get log timeline for charts
    /// </summary>
    LogTimeline GetTimeline(DateTime from, DateTime to, int intervalSeconds = 60);

    /// <summary>
    /// Get logs by trace ID
    /// </summary>
    IEnumerable<LogEntry> GetByTraceId(string traceId);

    /// <summary>
    /// Get distinct source contexts
    /// </summary>
    IEnumerable<string> GetSourceContexts();

    /// <summary>
    /// Clear old data based on retention policy
    /// </summary>
    void Cleanup();
}

