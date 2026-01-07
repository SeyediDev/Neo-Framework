using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Interface for in-memory trace storage
/// </summary>
public interface ITraceStore
{
    /// <summary>
    /// Record a span
    /// </summary>
    void RecordSpan(TraceSpan span);

    /// <summary>
    /// Get a trace by ID with all its spans
    /// </summary>
    TraceData? GetTrace(string traceId);

    /// <summary>
    /// Query traces
    /// </summary>
    IEnumerable<TraceData> Query(TraceQueryRequest request);

    /// <summary>
    /// Get recent traces
    /// </summary>
    IEnumerable<TraceData> GetRecentTraces(int limit = 100);

    /// <summary>
    /// Get trace statistics
    /// </summary>
    TraceStats GetStats(DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Get all service names
    /// </summary>
    IEnumerable<string> GetServiceNames();

    /// <summary>
    /// Get all operation names for a service
    /// </summary>
    IEnumerable<string> GetOperationNames(string? serviceName = null);

    /// <summary>
    /// Clear old data based on retention policy
    /// </summary>
    void Cleanup();

    /// <summary>
    /// Clear all traces
    /// </summary>
    void Clear();
}

