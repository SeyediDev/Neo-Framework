using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;

namespace Neo.Endpoint.Features.Monitoring.Controllers;

/// <summary>
/// API controller for monitoring dashboard data
/// </summary>
[ApiController]
[Route("api/monitoring/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IMetricsStore _metricsStore;
    private readonly ITraceStore _traceStore;
    private readonly ILogStore _logStore;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        IMetricsStore metricsStore,
        ITraceStore traceStore,
        ILogStore logStore,
        ILogger<DashboardController> logger)
    {
        _metricsStore = metricsStore;
        _traceStore = traceStore;
        _logStore = logStore;
        _logger = logger;
    }

    /// <summary>
    /// Get complete dashboard data
    /// </summary>
    [HttpGet]
    public ActionResult<MonitoringDashboardData> GetDashboard()
    {
        try
        {
            return Ok(_metricsStore.GetDashboardData());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard data");
            return StatusCode(500, new { error = "Failed to get dashboard data", message = ex.Message });
        }
    }

    /// <summary>
    /// Get dashboard summary with all data types
    /// </summary>
    [HttpGet("summary")]
    public ActionResult<DashboardSummary> GetSummary()
    {
        var dashboard = _metricsStore.GetDashboardData();
        var traceStats = _traceStore.GetStats();
        var logStats = _logStore.GetStats();

        return Ok(new DashboardSummary
        {
            GeneratedAt = DateTime.UtcNow,
            Metrics = dashboard,
            TraceStats = traceStats,
            LogStats = logStats
        });
    }

    /// <summary>
    /// Get health check status
    /// </summary>
    [HttpGet("health")]
    public ActionResult<HealthStatus> GetHealthStatus()
    {
        try
        {
            var system = _metricsStore.GetSystemMetrics();
            var app = _metricsStore.GetApplicationMetrics();

            var status = DetermineHealthStatus(system, app);

            return Ok(new HealthStatus
            {
                Status = status,
                Timestamp = DateTime.UtcNow,
                Uptime = system.Uptime,
                MemoryUsagePercent = system.MemoryUsagePercent,
                CpuUsagePercent = system.CpuUsagePercent,
                ErrorRate = 100 - app.SuccessRate,
                ActiveRequests = app.ActiveRequests,
                RequestsPerSecond = app.RequestsPerSecond
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting health status");
            return StatusCode(500, new { error = "Failed to get health status", message = ex.Message });
        }
    }

    private static string DetermineHealthStatus(SystemMetrics system, ApplicationMetrics app)
    {
        // Critical if error rate > 10% or memory > 95%
        if (app.SuccessRate < 90 || system.MemoryUsagePercent > 95)
            return "Critical";

        // Warning if error rate > 5% or memory > 80% or CPU > 80%
        if (app.SuccessRate < 95 || system.MemoryUsagePercent > 80 || system.CpuUsagePercent > 80)
            return "Warning";

        return "Healthy";
    }
}

/// <summary>
/// Complete dashboard summary
/// </summary>
public record DashboardSummary
{
    public required DateTime GeneratedAt { get; init; }
    public required MonitoringDashboardData Metrics { get; init; }
    public required TraceStats TraceStats { get; init; }
    public required LogStats LogStats { get; init; }
}

/// <summary>
/// Health status
/// </summary>
public record HealthStatus
{
    public required string Status { get; init; }
    public required DateTime Timestamp { get; init; }
    public TimeSpan Uptime { get; init; }
    public double MemoryUsagePercent { get; init; }
    public double CpuUsagePercent { get; init; }
    public double ErrorRate { get; init; }
    public int ActiveRequests { get; init; }
    public double RequestsPerSecond { get; init; }
}

