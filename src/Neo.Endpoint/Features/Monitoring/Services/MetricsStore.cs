using Neo.Endpoint.Features.Monitoring.Models;
using LogLevel = Neo.Endpoint.Features.Monitoring.Models.LogLevel;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// In-memory metrics storage with ring buffer for each metric
/// </summary>
public class MetricsStore : IMetricsStore
{
    private readonly MonitoringStorageOptions _options;
    private readonly ConcurrentDictionary<string, MetricDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MetricDataPoint>> _dataPoints = new();
    private readonly ConcurrentDictionary<string, int> _dataPointCounts = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private readonly ILogStore? _logStore;
    
    // CPU calculation state
    private DateTime _lastCpuCheckTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private double _lastCpuPercent = 0;

    public MetricsStore(IOptions<MonitoringStorageOptions> options, ILogStore? logStore = null)
    {
        _options = options?.Value ?? new MonitoringStorageOptions();
        _lastCpuTime = _currentProcess.TotalProcessorTime;
        _logStore = logStore;
    }

    public void Record(MetricDataPoint dataPoint)
    {
        // Update or create metric definition
        _definitions.AddOrUpdate(
            dataPoint.MetricName,
            _ => new MetricDefinition
            {
                Name = dataPoint.MetricName,
                Type = dataPoint.MetricType,
                Unit = dataPoint.Unit,
                Description = dataPoint.Description,
                FirstSeen = dataPoint.Timestamp,
                LastSeen = dataPoint.Timestamp,
                KnownTags = [.. dataPoint.Tags.Keys]
            },
            (_, existing) => existing with
            {
                LastSeen = dataPoint.Timestamp,
                KnownTags = [.. existing.KnownTags.Union(dataPoint.Tags.Keys)]
            });

        // Add data point to queue
        var queue = _dataPoints.GetOrAdd(dataPoint.MetricName, _ => new ConcurrentQueue<MetricDataPoint>());
        queue.Enqueue(dataPoint);

        // Track count and enforce limit
        var count = _dataPointCounts.AddOrUpdate(dataPoint.MetricName, 1, (_, c) => c + 1);
        
        // Remove oldest if over limit
        while (count > _options.MaxMetricDataPoints && queue.TryDequeue(out _))
        {
            count = _dataPointCounts.AddOrUpdate(dataPoint.MetricName, 0, (_, c) => Math.Max(0, c - 1));
        }
    }

    public IEnumerable<MetricDefinition> GetMetricDefinitions()
    {
        return _definitions.Values.OrderBy(d => d.Name);
    }

    public MetricDefinition? GetMetricDefinition(string metricName)
    {
        return _definitions.TryGetValue(metricName, out var def) ? def : null;
    }

    public IEnumerable<MetricDataPoint> Query(MetricsQueryRequest request)
    {
        IEnumerable<MetricDataPoint> result;

        if (!string.IsNullOrEmpty(request.MetricName))
        {
            if (_dataPoints.TryGetValue(request.MetricName, out var queue))
            {
                result = queue.ToArray();
            }
            else
            {
                return [];
            }
        }
        else
        {
            result = _dataPoints.Values.SelectMany(q => q.ToArray());
        }

        // Apply filters
        if (request.From.HasValue)
            result = result.Where(p => p.Timestamp >= request.From.Value);

        if (request.To.HasValue)
            result = result.Where(p => p.Timestamp <= request.To.Value);

        if (request.TagFilters?.Any() == true)
        {
            result = result.Where(p => request.TagFilters.All(f => 
                p.Tags.TryGetValue(f.Key, out var value) && value == f.Value));
        }

        // Order and limit
        result = result.OrderByDescending(p => p.Timestamp);

        if (request.Limit.HasValue)
            result = result.Take(request.Limit.Value);

        return result;
    }

    public MetricTimeSeries GetTimeSeries(string metricName, DateTime? from = null, DateTime? to = null,
        Dictionary<string, string>? tagFilters = null, int? aggregationIntervalSeconds = null)
    {
        var definition = GetMetricDefinition(metricName);
        if (definition == null)
        {
            return new MetricTimeSeries
            {
                MetricName = metricName,
                MetricType = "Unknown",
                DataPoints = []
            };
        }

        var points = Query(new MetricsQueryRequest
        {
            MetricName = metricName,
            From = from,
            To = to,
            TagFilters = tagFilters
        }).OrderBy(p => p.Timestamp);

        List<TimeSeriesPoint> dataPoints;

        if (aggregationIntervalSeconds.HasValue && aggregationIntervalSeconds.Value > 0)
        {
            // Aggregate by interval
            dataPoints = points
                .GroupBy(p => new DateTime(
                    p.Timestamp.Ticks / (TimeSpan.TicksPerSecond * aggregationIntervalSeconds.Value) 
                    * (TimeSpan.TicksPerSecond * aggregationIntervalSeconds.Value)))
                .Select(g => new TimeSeriesPoint
                {
                    Timestamp = g.Key,
                    Value = g.Average(p => p.Value)
                })
                .ToList();
        }
        else
        {
            dataPoints = points
                .Select(p => new TimeSeriesPoint
                {
                    Timestamp = p.Timestamp,
                    Value = p.Value
                })
                .ToList();
        }

        return new MetricTimeSeries
        {
            MetricName = metricName,
            MetricType = definition.Type,
            Unit = definition.Unit,
            DataPoints = dataPoints,
            Tags = tagFilters
        };
    }

    public MetricStats? GetStats(string metricName, DateTime? from = null, DateTime? to = null)
    {
        var definition = GetMetricDefinition(metricName);
        if (definition == null) return null;

        var points = Query(new MetricsQueryRequest
        {
            MetricName = metricName,
            From = from,
            To = to
        }).ToList();

        if (points.Count == 0) return null;

        var values = points.Select(p => p.Value).ToList();

        return new MetricStats
        {
            MetricName = metricName,
            MetricType = definition.Type,
            CurrentValue = points.First().Value, // Most recent (already ordered desc)
            MinValue = values.Min(),
            MaxValue = values.Max(),
            AvgValue = values.Average(),
            SumValue = values.Sum(),
            Count = values.Count,
            FirstTimestamp = points.Last().Timestamp,
            LastTimestamp = points.First().Timestamp
        };
    }

    public SystemMetrics GetSystemMetrics()
    {
        _currentProcess.Refresh();
        
        var gcInfo = GC.GetGCMemoryInfo();
        
        return new SystemMetrics
        {
            CpuUsagePercent = GetCpuUsage(),
            MemoryUsedBytes = _currentProcess.WorkingSet64,
            MemoryTotalBytes = gcInfo.TotalAvailableMemoryBytes,
            MemoryUsagePercent = gcInfo.TotalAvailableMemoryBytes > 0 
                ? (double)_currentProcess.WorkingSet64 / gcInfo.TotalAvailableMemoryBytes * 100 
                : 0,
            ThreadCount = _currentProcess?.Threads?.Count??0,
            Uptime = _uptime.Elapsed,
            GcGen0Collections = GC.CollectionCount(0),
            GcGen1Collections = GC.CollectionCount(1),
            GcGen2Collections = GC.CollectionCount(2),
            GcTotalMemory = GC.GetTotalMemory(false)
        };
    }

    public ApplicationMetrics GetApplicationMetrics()
    {
        // Try custom metric names first, then fall back to ASP.NET Core built-in names
        var totalStats = GetStats("request.total") 
                      ?? GetStats("http.server.request.duration")
                      ?? GetStats("http-server-request-duration");
        
        var successStats = GetStats("request.success");
        var failureStats = GetStats("request.failure");
        
        var durationStats = GetStats("request.duration") 
                         ?? GetStats("http.server.request.duration")
                         ?? GetStats("http-server-request-duration");
        
        var inflightStats = GetStats("request.inflights") 
                         ?? GetStats("http.server.active_requests")
                         ?? GetStats("http-server-active-requests")
                         ?? GetStats("kestrel.current_connections")
                         ?? GetStats("kestrel-current-connections");

        var total = (long)(totalStats?.Count ?? 0);
        var success = (long)(successStats?.SumValue ?? total); // Assume all successful if no failure tracking
        var failure = (long)(failureStats?.SumValue ?? 0);

        // Calculate requests per second (last minute)
        var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);

        // If we don't have enough metrics data, try to calculate from logs
        if (total == 0 && _logStore != null)
        {
            var recentLogs = _logStore.GetRecentLogs(1000).Where(l => l.Timestamp >= oneMinuteAgo).ToList();
            
            // Count total requests from logs (any log entry can represent a request)
            total = recentLogs.Count;
            
            // Count failures from Error and Critical level logs
            failure = recentLogs.Count(l => l.Level == LogLevel.Error || l.Level == LogLevel.Critical);
            success = total - failure;
        }
        var recentCount = 0;
        
        // Try different metric names for recent request count
        foreach (var metricName in new[] { "request.total", "http.server.request.duration", "http-server-request-duration" })
        {
            var requests = Query(new MetricsQueryRequest
            {
                MetricName = metricName,
                From = oneMinuteAgo
            });
            recentCount = requests.Count();
            if (recentCount > 0) break;
        }

        // If we still don't have recent count, use logs
        if (recentCount == 0 && _logStore != null)
        {
            var recentLogs = _logStore.GetRecentLogs(1000).Where(l => l.Timestamp >= oneMinuteAgo).ToList();
            recentCount = recentLogs.Count;
        }

        // Calculate success rate
        var successRate = 100.0;
        if (total > 0)
        {
            if (failure > 0)
            {
                successRate = (double)(total - failure) / total * 100;
            }
            // If we have total but no explicit failure tracking, check if we can infer from logs
            else if (_logStore != null && successStats == null && failureStats == null)
            {
                // Try to get error count from logs in the last minute
                var oneMinuteAgoForErrors = DateTime.UtcNow.AddMinutes(-1);
                var errorLogs = _logStore.Query(new LogQueryRequest
                {
                    From = oneMinuteAgoForErrors,
                    MinLevel = LogLevel.Error,
                    Limit = null
                }).ToList();
                
                var errorCount = errorLogs.Count;
                if (errorCount > 0 && total > 0)
                {
                    // Estimate: assume each error log represents a failed request
                    failure = errorCount;
                    success = Math.Max(0, total - failure);
                    successRate = (double)(total - failure) / total * 100;
                }
            }
        }

        return new ApplicationMetrics
        {
            TotalRequests = total,
            SuccessfulRequests = success,
            FailedRequests = failure,
            SuccessRate = successRate,
            AverageResponseTimeMs = durationStats?.AvgValue ?? 0,
            ActiveRequests = (int)(inflightStats?.CurrentValue ?? inflightStats?.Count ?? 0),
            RequestsPerSecond = recentCount / 60.0
        };
    }

    public MonitoringDashboardData GetDashboardData()
    {
        var topMetrics = _definitions.Keys
            .Select(name => GetStats(name))
            .Where(s => s != null)
            .OrderByDescending(s => s!.Count)
            .Take(10)
            .ToList();

        return new MonitoringDashboardData
        {
            GeneratedAt = DateTime.UtcNow,
            System = GetSystemMetrics(),
            Application = GetApplicationMetrics(),
            TopMetrics = topMetrics!
        };
    }

    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow - _options.MetricsRetention;

        foreach (var kvp in _dataPoints)
        {
            var queue = kvp.Value;
            var metricName = kvp.Key;

            // Remove old entries
            while (queue.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            {
                if (queue.TryDequeue(out _))
                {
                    _dataPointCounts.AddOrUpdate(metricName, 0, (_, c) => Math.Max(0, c - 1));
                }
            }
        }
    }

    public void Reset()
    {
        _dataPoints.Clear();
        _dataPointCounts.Clear();
        // Keep definitions but reset their timestamps
        foreach (var metricName in _definitions.Keys.ToList())
        {
            _definitions[metricName] = _definitions[metricName] with
            {
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };
        }
    }

    private double GetCpuUsage()
    {
        try
        {
            var currentTime = DateTime.UtcNow;
            _currentProcess.Refresh();
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
}

