using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// In-memory trace storage
/// </summary>
public class TraceStore : ITraceStore
{
    private readonly MonitoringStorageOptions _options;
    
    // TraceId -> List of spans
    private readonly ConcurrentDictionary<string, ConcurrentBag<TraceSpan>> _traces = new();
    private readonly ConcurrentQueue<string> _traceOrder = new(); // For maintaining order and limiting
    private int _traceCount;

    public TraceStore(IOptions<MonitoringStorageOptions> options)
    {
        _options = options?.Value ?? new MonitoringStorageOptions();
    }

    public void RecordSpan(TraceSpan span)
    {
        var spans = _traces.GetOrAdd(span.TraceId, _ =>
        {
            _traceOrder.Enqueue(span.TraceId);
            Interlocked.Increment(ref _traceCount);
            return [];
        });

        spans.Add(span);

        // Enforce limit
        while (_traceCount > _options.MaxTraces && _traceOrder.TryDequeue(out var oldTraceId))
        {
            if (_traces.TryRemove(oldTraceId, out _))
            {
                Interlocked.Decrement(ref _traceCount);
            }
        }
    }

    public TraceData? GetTrace(string traceId)
    {
        if (!_traces.TryGetValue(traceId, out var spans) || spans.IsEmpty)
            return null;

        return BuildTrace(traceId, [.. spans]);
    }

    public IEnumerable<TraceData> Query(TraceQueryRequest request)
    {
        IEnumerable<KeyValuePair<string, ConcurrentBag<TraceSpan>>> traces = _traces;

        // Filter by trace ID
        if (!string.IsNullOrEmpty(request.TraceId))
        {
            if (_traces.TryGetValue(request.TraceId, out var spans))
            {
                var trace = BuildTrace(request.TraceId, [.. spans]);
                return trace != null ? [trace] : [];
            }
            return [];
        }

        var result = traces
            .Select(kvp => BuildTrace(kvp.Key, [.. kvp.Value]))
            .Where(t => t != null)
            .Cast<TraceData>();

        // Apply filters
        if (!string.IsNullOrEmpty(request.ServiceName))
            result = result.Where(t => t.ServiceName == request.ServiceName);

        if (!string.IsNullOrEmpty(request.OperationName))
            result = result.Where(t => t.RootOperationName.Contains(request.OperationName, StringComparison.OrdinalIgnoreCase));

        if (request.From.HasValue)
            result = result.Where(t => t.StartTime >= request.From.Value);

        if (request.To.HasValue)
            result = result.Where(t => t.EndTime <= request.To.Value);

        if (request.MinDuration.HasValue)
            result = result.Where(t => t.Duration >= request.MinDuration.Value);

        if (request.MaxDuration.HasValue)
            result = result.Where(t => t.Duration <= request.MaxDuration.Value);

        if (request.Status.HasValue)
            result = result.Where(t => t.Status == request.Status.Value);

        if (request.TagFilters?.Any() == true)
        {
            result = result.Where(t => t.Spans.Any(s => 
                request.TagFilters.All(f => s.Tags.TryGetValue(f.Key, out var v) && v == f.Value)));
        }

        // Order
        result = request.OrderBy?.ToLower() switch
        {
            "duration" => request.Descending 
                ? result.OrderByDescending(t => t.Duration) 
                : result.OrderBy(t => t.Duration),
            "spancount" => request.Descending 
                ? result.OrderByDescending(t => t.SpanCount) 
                : result.OrderBy(t => t.SpanCount),
            _ => request.Descending 
                ? result.OrderByDescending(t => t.StartTime) 
                : result.OrderBy(t => t.StartTime)
        };

        if (request.Limit.HasValue)
            result = result.Take(request.Limit.Value);

        return result;
    }

    public IEnumerable<TraceData> GetRecentTraces(int limit = 100)
    {
        return Query(new TraceQueryRequest { Limit = limit, Descending = true });
    }

    public TraceStats GetStats(DateTime? from = null, DateTime? to = null)
    {
        var traces = Query(new TraceQueryRequest { From = from, To = to, Limit = null }).ToList();

        if (traces.Count == 0)
        {
            return new TraceStats();
        }

        var durations = traces.Select(t => t.Duration.TotalMilliseconds).OrderBy(d => d).ToList();
        var errorTraces = traces.Count(t => t.Status == SpanStatus.Error);

        return new TraceStats
        {
            TotalTraces = traces.Count,
            TotalSpans = traces.Sum(t => t.SpanCount),
            ErrorTraces = errorTraces,
            ErrorRate = traces.Count > 0 ? (double)errorTraces / traces.Count * 100 : 0,
            AvgDurationMs = durations.Average(),
            MinDurationMs = durations.Min(),
            MaxDurationMs = durations.Max(),
            P50DurationMs = GetPercentile(durations, 50),
            P90DurationMs = GetPercentile(durations, 90),
            P99DurationMs = GetPercentile(durations, 99),
            ServiceCounts = traces.GroupBy(t => t.ServiceName).ToDictionary(g => g.Key, g => g.Count()),
            OperationCounts = traces.GroupBy(t => t.RootOperationName).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    public IEnumerable<string> GetServiceNames()
    {
        return _traces.Values
            .SelectMany(spans => spans.Select(s => s.ServiceName))
            .Distinct()
            .OrderBy(s => s);
    }

    public IEnumerable<string> GetOperationNames(string? serviceName = null)
    {
        var spans = _traces.Values.SelectMany(s => s);
        
        if (!string.IsNullOrEmpty(serviceName))
            spans = spans.Where(s => s.ServiceName == serviceName);

        return spans
            .Select(s => s.OperationName)
            .Distinct()
            .OrderBy(s => s);
    }

    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow - _options.TracesRetention;
        var tracesToRemove = new List<string>();

        foreach (var kvp in _traces)
        {
            var spans = kvp.Value;
            if (spans.All(s => s.EndTime < cutoff))
            {
                tracesToRemove.Add(kvp.Key);
            }
        }

        foreach (var traceId in tracesToRemove)
        {
            if (_traces.TryRemove(traceId, out _))
            {
                Interlocked.Decrement(ref _traceCount);
            }
        }
    }

    private static TraceData? BuildTrace(string traceId, List<TraceSpan> spans)
    {
        if (spans.Count == 0) return null;

        // Find root span (no parent or parent not in this trace)
        var spanIds = spans.Select(s => s.SpanId).ToHashSet();
        var rootSpan = spans.FirstOrDefault(s => 
            string.IsNullOrEmpty(s.ParentSpanId) || !spanIds.Contains(s.ParentSpanId))
            ?? spans.OrderBy(s => s.StartTime).First();

        var hasError = spans.Any(s => s.Status == SpanStatus.Error);

        return new TraceData
        {
            TraceId = traceId,
            RootOperationName = rootSpan.OperationName,
            ServiceName = rootSpan.ServiceName,
            StartTime = spans.Min(s => s.StartTime),
            EndTime = spans.Max(s => s.EndTime),
            Status = hasError ? SpanStatus.Error : SpanStatus.Ok,
            SpanCount = spans.Count,
            ErrorCount = spans.Count(s => s.Status == SpanStatus.Error),
            Spans = [.. spans.OrderBy(s => s.StartTime)]
        };
    }

    private static double GetPercentile(List<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Count) - 1;
        return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
    }
}

