using Neo.Endpoint.Features.Monitoring.Models;
using LogLevel = Neo.Endpoint.Features.Monitoring.Models.LogLevel;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// In-memory log storage with ring buffer
/// </summary>
public class LogStore : ILogStore
{
    private readonly MonitoringStorageOptions _options;
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private int _logCount;

    public LogStore(IOptions<MonitoringStorageOptions> options)
    {
        _options = options?.Value ?? new MonitoringStorageOptions();
    }

    public void Record(LogEntry entry)
    {
        _logs.Enqueue(entry);
        Interlocked.Increment(ref _logCount);

        // Enforce limit
        while (_logCount > _options.MaxLogEntries && _logs.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _logCount);
        }
    }

    public IEnumerable<LogEntry> Query(LogQueryRequest request)
    {
        IEnumerable<LogEntry> result = _logs.ToArray();

        // Apply filters
        if (request.From.HasValue)
            result = result.Where(l => l.Timestamp >= request.From.Value);

        if (request.To.HasValue)
            result = result.Where(l => l.Timestamp <= request.To.Value);

        if (request.MinLevel.HasValue)
            result = result.Where(l => l.Level >= request.MinLevel.Value);

        if (request.MaxLevel.HasValue)
            result = result.Where(l => l.Level <= request.MaxLevel.Value);

        if (!string.IsNullOrEmpty(request.SourceContext))
            result = result.Where(l => l.SourceContext?.Contains(request.SourceContext, StringComparison.OrdinalIgnoreCase) == true);

        if (!string.IsNullOrEmpty(request.TraceId))
            result = result.Where(l => l.TraceId == request.TraceId);

        if (!string.IsNullOrEmpty(request.MessageContains))
            result = result.Where(l => l.Message.Contains(request.MessageContains, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(request.UserId))
            result = result.Where(l => l.UserId == request.UserId);

        if (request.HasException.HasValue)
        {
            result = request.HasException.Value
                ? result.Where(l => !string.IsNullOrEmpty(l.ExceptionType))
                : result.Where(l => string.IsNullOrEmpty(l.ExceptionType));
        }

        // Order
        result = request.Descending
            ? result.OrderByDescending(l => l.Timestamp)
            : result.OrderBy(l => l.Timestamp);

        if (request.Limit.HasValue)
            result = result.Take(request.Limit.Value);

        return result;
    }

    public IEnumerable<LogEntry> GetRecentLogs(int limit = 100, LogLevel? minLevel = null)
    {
        return Query(new LogQueryRequest
        {
            MinLevel = minLevel,
            Limit = limit,
            Descending = true
        });
    }

    public LogStats GetStats(DateTime? from = null, DateTime? to = null)
    {
        var logs = Query(new LogQueryRequest { From = from, To = to, Limit = null }).ToList();

        if (logs.Count == 0)
        {
            return new LogStats();
        }

        return new LogStats
        {
            TotalLogs = logs.Count,
            CountByLevel = logs.GroupBy(l => l.Level).ToDictionary(g => g.Key, g => g.Count()),
            ExceptionCount = logs.Count(l => !string.IsNullOrEmpty(l.ExceptionType)),
            OldestEntry = logs.Min(l => l.Timestamp),
            NewestEntry = logs.Max(l => l.Timestamp),
            TopSourceContexts = logs
                .Where(l => !string.IsNullOrEmpty(l.SourceContext))
                .GroupBy(l => l.SourceContext!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count()),
            TopExceptionTypes = logs
                .Where(l => !string.IsNullOrEmpty(l.ExceptionType))
                .GroupBy(l => l.ExceptionType!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    public LogTimeline GetTimeline(DateTime from, DateTime to, int intervalSeconds = 60)
    {
        var logs = Query(new LogQueryRequest { From = from, To = to, Limit = null }).ToList();
        var buckets = new List<LogTimelineBucket>();

        for (var timestamp = from; timestamp < to; timestamp = timestamp.AddSeconds(intervalSeconds))
        {
            var bucketEnd = timestamp.AddSeconds(intervalSeconds);
            var bucketLogs = logs.Where(l => l.Timestamp >= timestamp && l.Timestamp < bucketEnd).ToList();

            buckets.Add(new LogTimelineBucket
            {
                Timestamp = timestamp,
                TotalCount = bucketLogs.Count,
                TraceCount = bucketLogs.Count(l => l.Level == LogLevel.Trace),
                DebugCount = bucketLogs.Count(l => l.Level == LogLevel.Debug),
                InfoCount = bucketLogs.Count(l => l.Level == LogLevel.Information),
                WarnCount = bucketLogs.Count(l => l.Level == LogLevel.Warning),
                ErrorCount = bucketLogs.Count(l => l.Level == LogLevel.Error),
                CriticalCount = bucketLogs.Count(l => l.Level == LogLevel.Critical)
            });
        }

        return new LogTimeline
        {
            From = from,
            To = to,
            IntervalSeconds = intervalSeconds,
            Buckets = buckets
        };
    }

    public IEnumerable<LogEntry> GetByTraceId(string traceId)
    {
        return Query(new LogQueryRequest { TraceId = traceId, Limit = null });
    }

    public IEnumerable<string> GetSourceContexts()
    {
        return _logs
            .Select(l => l.SourceContext)
            .Where(s => !string.IsNullOrEmpty(s))
            .Cast<string>()
            .Distinct()
            .OrderBy(s => s);
    }

    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow - _options.LogsRetention;

        while (_logs.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            if (_logs.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _logCount);
            }
        }
    }

    public void Clear()
    {
        while (_logs.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _logCount);
        }
        _logCount = 0;
    }
}

