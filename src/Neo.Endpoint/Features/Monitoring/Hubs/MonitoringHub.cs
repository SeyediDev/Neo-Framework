using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;

namespace Neo.Endpoint.Features.Monitoring.Hubs;

/// <summary>
/// SignalR hub for real-time monitoring updates
/// </summary>
public class MonitoringHub : Hub
{
    private readonly IMetricsStore _metricsStore;
    private readonly ITraceStore _traceStore;
    private readonly ILogStore _logStore;
    private readonly ILogger<MonitoringHub> _logger;

    public MonitoringHub(
        IMetricsStore metricsStore,
        ITraceStore traceStore,
        ILogStore logStore,
        ILogger<MonitoringHub> logger)
    {
        _metricsStore = metricsStore;
        _traceStore = traceStore;
        _logStore = logStore;
        _logger = logger;
    }

    /// <summary>
    /// Subscribe to dashboard updates
    /// </summary>
    public async Task SubscribeToDashboard()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
        _logger.LogDebug("Client {ConnectionId} subscribed to dashboard", Context.ConnectionId);
    }

    /// <summary>
    /// Unsubscribe from dashboard updates
    /// </summary>
    public async Task UnsubscribeFromDashboard()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "dashboard");
        _logger.LogDebug("Client {ConnectionId} unsubscribed from dashboard", Context.ConnectionId);
    }

    /// <summary>
    /// Subscribe to specific metric updates
    /// </summary>
    public async Task SubscribeToMetric(string metricName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"metric:{metricName}");
        _logger.LogDebug("Client {ConnectionId} subscribed to metric {MetricName}", Context.ConnectionId, metricName);
    }

    /// <summary>
    /// Unsubscribe from specific metric updates
    /// </summary>
    public async Task UnsubscribeFromMetric(string metricName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"metric:{metricName}");
        _logger.LogDebug("Client {ConnectionId} unsubscribed from metric {MetricName}", Context.ConnectionId, metricName);
    }

    /// <summary>
    /// Subscribe to log updates
    /// </summary>
    public async Task SubscribeToLogs(string? minLevel = null)
    {
        var group = string.IsNullOrEmpty(minLevel) ? "logs" : $"logs:{minLevel}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        _logger.LogDebug("Client {ConnectionId} subscribed to logs (level: {Level})", Context.ConnectionId, minLevel ?? "all");
    }

    /// <summary>
    /// Unsubscribe from log updates
    /// </summary>
    public async Task UnsubscribeFromLogs(string? minLevel = null)
    {
        var group = string.IsNullOrEmpty(minLevel) ? "logs" : $"logs:{minLevel}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from logs", Context.ConnectionId);
    }

    /// <summary>
    /// Get current dashboard data
    /// </summary>
    public MonitoringDashboardData GetDashboard()
    {
        return _metricsStore.GetDashboardData();
    }

    /// <summary>
    /// Get recent traces
    /// </summary>
    public IEnumerable<TraceData> GetRecentTraces(int limit = 50)
    {
        return _traceStore.GetRecentTraces(limit);
    }

    /// <summary>
    /// Get recent logs
    /// </summary>
    public IEnumerable<LogEntry> GetRecentLogs(int limit = 100, string? minLevel = null)
    {
        Models.LogLevel? level = null;
        if (!string.IsNullOrEmpty(minLevel) && Enum.TryParse<Models.LogLevel>(minLevel, true, out var parsed))
        {
            level = parsed;
        }
        return _logStore.GetRecentLogs(limit, level);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Service to broadcast monitoring updates via SignalR
/// </summary>
public class MonitoringBroadcaster : BackgroundService
{
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly IMetricsStore _metricsStore;
    private readonly ILogger<MonitoringBroadcaster> _logger;
    private readonly TimeSpan _broadcastInterval = TimeSpan.FromSeconds(1);

    public MonitoringBroadcaster(
        IHubContext<MonitoringHub> hubContext,
        IMetricsStore metricsStore,
        ILogger<MonitoringBroadcaster> logger)
    {
        _hubContext = hubContext;
        _metricsStore = metricsStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringBroadcaster starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_broadcastInterval, stoppingToken);

                // Broadcast dashboard updates
                var dashboard = _metricsStore.GetDashboardData();
                await _hubContext.Clients.Group("dashboard").SendAsync("DashboardUpdate", dashboard, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting monitoring updates");
            }
        }

        _logger.LogInformation("MonitoringBroadcaster stopping...");
    }
}

