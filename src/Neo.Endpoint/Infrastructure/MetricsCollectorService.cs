using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Neo.Endpoint.Infrastructure;

/// <summary>
/// Background service برای جمع‌آوری metrics از سیستم و اضافه کردن به MonitoringDataStore
/// </summary>
public class MetricsCollectorService : BackgroundService
{
    private readonly ILogger<MetricsCollectorService> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _requestCounter;
    private readonly Histogram<double> _responseTimeHistogram;
    private readonly ObservableGauge<double> _cpuGauge;
    private readonly ObservableGauge<double> _memoryGauge;

    public MetricsCollectorService(ILogger<MetricsCollectorService> logger)
    {
        _logger = logger;
        _meter = new Meter("Neo.Monitoring", "1.0.0");
        
        // ایجاد metrics
        _requestCounter = _meter.CreateCounter<long>("http_requests_total", "requests", "Total number of HTTP requests");
        _responseTimeHistogram = _meter.CreateHistogram<double>("http_response_time_ms", "ms", "HTTP response time");
        
        // CPU و Memory gauges
        _cpuGauge = _meter.CreateObservableGauge<double>("system_cpu_usage_percent", () => GetCpuUsage(), "%", "CPU usage percentage");
        _memoryGauge = _meter.CreateObservableGauge<double>("system_memory_usage_percent", () => GetMemoryUsage(), "%", "Memory usage percentage");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectMetrics();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // هر 5 ثانیه
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error collecting metrics");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private Task CollectMetrics()
    {
        // جمع‌آوری CPU و Memory
        var cpuUsage = GetCpuUsage();
        var memoryUsage = GetMemoryUsage();
        
        // اضافه کردن به MonitoringDataStore
        MonitoringDataStore.AddMetric("cpu.usage", new
        {
            Name = "cpu.usage",
            DisplayName = "مصرف CPU",
            Unit = "%",
            Description = "درصد مصرف CPU",
            Type = "Gauge",
            Stats = new { CurrentValue = cpuUsage, AvgValue = cpuUsage, MinValue = 0, MaxValue = 100, SumValue = cpuUsage, Count = 1 }
        });
        
        MonitoringDataStore.AddMetric("memory.usage", new
        {
            Name = "memory.usage",
            DisplayName = "مصرف حافظه",
            Unit = "%",
            Description = "درصد مصرف حافظه",
            Type = "Gauge",
            Stats = new { CurrentValue = memoryUsage, AvgValue = memoryUsage, MinValue = 0, MaxValue = 100, SumValue = memoryUsage, Count = 1 }
        });
        
        // اضافه کردن metrics دیگر
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64 / (1024.0 * 1024.0); // MB
        
        MonitoringDataStore.AddMetric("process.memory", new
        {
            Name = "process.memory",
            DisplayName = "حافظه پردازش",
            Unit = "MB",
            Description = "حافظه استفاده شده توسط پردازش",
            Type = "Gauge",
            Stats = new { CurrentValue = workingSet, AvgValue = workingSet, MinValue = 0, MaxValue = workingSet * 2, SumValue = workingSet, Count = 1 }
        });
        
        var threadCount = process.Threads.Count;
        MonitoringDataStore.AddMetric("process.threads", new
        {
            Name = "process.threads",
            DisplayName = "تعداد Thread ها",
            Unit = "count",
            Description = "تعداد thread های فعال",
            Type = "Gauge",
            Stats = new { CurrentValue = threadCount, AvgValue = threadCount, MinValue = 0, MaxValue = threadCount * 2, SumValue = threadCount, Count = 1 }
        });
        
        return Task.CompletedTask;
    }

    private static double GetCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var startTime = DateTime.UtcNow;
            var startCpu = process.TotalProcessorTime;
            
            Thread.Sleep(100);
            
            var endTime = DateTime.UtcNow;
            var endCpu = process.TotalProcessorTime;
            var cpuUsedMs = (endCpu - startCpu).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            
            if (totalMsPassed > 0)
            {
                var cpuUsagePercent = (cpuUsedMs / (totalMsPassed * Environment.ProcessorCount)) * 100;
                return Math.Min(100, Math.Max(0, cpuUsagePercent));
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetMemoryUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var totalMemory = GC.GetTotalMemory(false) / (1024.0 * 1024.0); // MB
            var workingSet = process.WorkingSet64 / (1024.0 * 1024.0); // MB
            
            // تخمین درصد استفاده از حافظه
            var totalPhysicalMemory = GetTotalPhysicalMemory();
            if (totalPhysicalMemory > 0)
            {
                return Math.Min(100, (workingSet / totalPhysicalMemory) * 100);
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetTotalPhysicalMemory()
    {
        try
        {
            // Windows - استفاده از GC برای تخمین
            var process = Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64 / (1024.0 * 1024.0); // MB
            
            // تخمین ساده: فرض می‌کنیم که حداکثر 8GB RAM داریم
            // در production می‌توان از WMI یا PerformanceCounter استفاده کرد
            return 8192; // 8GB به MB
        }
        catch
        {
            return 8192; // Fallback
        }
    }

    public override void Dispose()
    {
        _meter?.Dispose();
        base.Dispose();
    }
}

