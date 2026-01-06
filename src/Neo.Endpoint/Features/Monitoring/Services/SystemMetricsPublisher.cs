using Microsoft.Extensions.Options;
using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Background service that publishes built-in system metrics
/// </summary>
public class SystemMetricsPublisher : BackgroundService
{
    private readonly ILogger<SystemMetricsPublisher> _logger;
    private readonly MonitoringStorageOptions _options;
    private readonly Meter _meter;
    private readonly Process _currentProcess;
    
    // Observables
    private ObservableGauge<double>? _cpuGauge;
    private ObservableGauge<long>? _memoryUsedGauge;
    private ObservableGauge<long>? _memoryTotalGauge;
    private ObservableGauge<double>? _memoryPercentGauge;
    private ObservableGauge<int>? _threadCountGauge;
    private ObservableGauge<long>? _gcMemoryGauge;
    private ObservableGauge<int>? _gcGen0Gauge;
    private ObservableGauge<int>? _gcGen1Gauge;
    private ObservableGauge<int>? _gcGen2Gauge;
    
    // CPU calculation state
    private DateTime _lastCpuCheckTime;
    private TimeSpan _lastCpuTime;
    private double _lastCpuPercent;

    public SystemMetricsPublisher(
        IOptions<MonitoringStorageOptions> options,
        ILogger<SystemMetricsPublisher> logger)
    {
        _options = options?.Value ?? new MonitoringStorageOptions();
        _logger = logger;
        _currentProcess = Process.GetCurrentProcess();
        _meter = new Meter("Neo.Bpms.System", "1.0.0");
        
        _lastCpuCheckTime = DateTime.UtcNow;
        _lastCpuTime = _currentProcess.TotalProcessorTime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SystemMetricsPublisher starting...");

        // Create observable gauges
        _cpuGauge = _meter.CreateObservableGauge(
            "system.cpu.usage",
            GetCpuUsage,
            unit: "%",
            description: "Process CPU usage percentage");

        _memoryUsedGauge = _meter.CreateObservableGauge(
            "system.memory.used",
            () => _currentProcess.WorkingSet64,
            unit: "bytes",
            description: "Process memory usage in bytes");

        _memoryTotalGauge = _meter.CreateObservableGauge(
            "system.memory.total",
            () => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            unit: "bytes",
            description: "Total available memory in bytes");

        _memoryPercentGauge = _meter.CreateObservableGauge(
            "system.memory.percent",
            GetMemoryPercent,
            unit: "%",
            description: "Process memory usage percentage");

        _threadCountGauge = _meter.CreateObservableGauge(
            "system.threads",
            () => _currentProcess.Threads.Count,
            unit: "threads",
            description: "Process thread count");

        _gcMemoryGauge = _meter.CreateObservableGauge(
            "dotnet.gc.memory",
            () => GC.GetTotalMemory(false),
            unit: "bytes",
            description: ".NET GC managed memory");

        _gcGen0Gauge = _meter.CreateObservableGauge(
            "dotnet.gc.gen0",
            () => GC.CollectionCount(0),
            unit: "collections",
            description: ".NET GC Gen 0 collections");

        _gcGen1Gauge = _meter.CreateObservableGauge(
            "dotnet.gc.gen1",
            () => GC.CollectionCount(1),
            unit: "collections",
            description: ".NET GC Gen 1 collections");

        _gcGen2Gauge = _meter.CreateObservableGauge(
            "dotnet.gc.gen2",
            () => GC.CollectionCount(2),
            unit: "collections",
            description: ".NET GC Gen 2 collections");

        _logger.LogInformation("SystemMetricsPublisher started. Publishing system metrics...");

        // Keep the service running
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Refresh process info periodically
                _currentProcess.Refresh();
                await Task.Delay(_options.MetricsCollectionIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in system metrics publisher loop");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("SystemMetricsPublisher stopping...");
    }

    private double GetCpuUsage()
    {
        try
        {
            var currentTime = DateTime.UtcNow;
            var currentCpuTime = _currentProcess.TotalProcessorTime;

            var timeDiff = (currentTime - _lastCpuCheckTime).TotalMilliseconds;
            if (timeDiff < 100) // Too short interval, return cached value
            {
                return _lastCpuPercent;
            }

            var cpuDiff = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            _lastCpuPercent = cpuDiff / (timeDiff * Environment.ProcessorCount) * 100;

            _lastCpuCheckTime = currentTime;
            _lastCpuTime = currentCpuTime;

            return Math.Max(0, Math.Min(100, _lastCpuPercent)); // Clamp to 0-100
        }
        catch
        {
            return _lastCpuPercent;
        }
    }

    private double GetMemoryPercent()
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                return (double)_currentProcess.WorkingSet64 / gcInfo.TotalAvailableMemoryBytes * 100;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}

