namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Background service that periodically cleans up old monitoring data
/// </summary>
public class MonitoringCleanupService : BackgroundService
{
    private readonly IMetricsStore _metricsStore;
    private readonly ITraceStore _traceStore;
    private readonly ILogStore _logStore;
    private readonly ILogger<MonitoringCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);

    public MonitoringCleanupService(
        IMetricsStore metricsStore,
        ITraceStore traceStore,
        ILogStore logStore,
        ILogger<MonitoringCleanupService> logger)
    {
        _metricsStore = metricsStore;
        _traceStore = traceStore;
        _logStore = logStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringCleanupService starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                _logger.LogDebug("Running monitoring data cleanup...");

                _metricsStore.Cleanup();
                _traceStore.Cleanup();
                _logStore.Cleanup();

                _logger.LogDebug("Monitoring data cleanup completed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during monitoring cleanup");
            }
        }

        _logger.LogInformation("MonitoringCleanupService stopping...");
    }
}

