using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Neo.Endpoint.Infrastructure;

/// <summary>
/// Store ساده برای ذخیره داده‌های مانیتورینگ (لاگ‌ها، metrics، traces)
/// </summary>
public static class MonitoringDataStore
{
    // Store برای لاگ‌ها (حداکثر 10000 لاگ)
    private static readonly ConcurrentQueue<object> _logs = new();
    private static readonly int MaxLogs = 10000;

    // Store برای metrics
    private static readonly ConcurrentDictionary<string, object> _metrics = new();

    // Store برای traces (حداکثر 5000 trace)
    private static readonly ConcurrentQueue<object> _traces = new();
    private static readonly int MaxTraces = 5000;

    /// <summary>
    /// اضافه کردن لاگ به store
    /// </summary>
    public static void AddLog(object logEntry)
    {
        _logs.Enqueue(logEntry);
        
        // حذف لاگ‌های قدیمی اگر تعداد از حد مجاز بیشتر شد
        while (_logs.Count > MaxLogs)
        {
            _logs.TryDequeue(out _);
        }
    }

    /// <summary>
    /// دریافت لاگ‌های اخیر
    /// </summary>
    public static List<object> GetRecentLogs(int limit = 50, int? minLevel = null, string? correlationId = null, string? source = null, string? textSearch = null)
    {
        var logs = Enumerable.Reverse(_logs.ToArray()).ToList();
        
        // فیلتر بر اساس minLevel
        if (minLevel.HasValue)
        {
            logs = logs.Where(log =>
            {
                var level = GetLogLevel(log);
                return level >= minLevel.Value;
            }).ToList();
        }
        
        // فیلتر بر اساس correlationId
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            logs = logs.Where(log =>
            {
                var corrId = GetLogProperty(log, "CorrelationId") ?? GetLogProperty(log, "CorelationId");
                return corrId?.ToString()?.Contains(correlationId, StringComparison.OrdinalIgnoreCase) == true;
            }).ToList();
        }
        
        // فیلتر بر اساس source
        if (!string.IsNullOrWhiteSpace(source))
        {
            logs = logs.Where(log =>
            {
                var logSource = GetLogProperty(log, "SourceContext") ?? GetLogProperty(log, "CategoryName");
                return logSource?.ToString()?.Equals(source, StringComparison.OrdinalIgnoreCase) == true;
            }).ToList();
        }
        
        // فیلتر بر اساس textSearch
        if (!string.IsNullOrWhiteSpace(textSearch))
        {
            logs = logs.Where(log =>
            {
                var message = GetLogProperty(log, "Message")?.ToString() ?? "";
                var sourceContext = GetLogProperty(log, "SourceContext")?.ToString() ?? "";
                return message.Contains(textSearch, StringComparison.OrdinalIgnoreCase) ||
                       sourceContext.Contains(textSearch, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        
        return logs.Take(limit).ToList();
    }

    /// <summary>
    /// اضافه کردن metric به store
    /// </summary>
    public static void AddMetric(string name, object metric)
    {
        _metrics.AddOrUpdate(name, metric, (key, oldValue) => metric);
    }

    /// <summary>
    /// دریافت همه metrics
    /// </summary>
    public static List<object> GetAllMetrics()
    {
        return _metrics.Values.ToList();
    }

    /// <summary>
    /// دریافت metric با نام مشخص
    /// </summary>
    public static object? GetMetric(string name)
    {
        return _metrics.TryGetValue(name, out var metric) ? metric : null;
    }

    /// <summary>
    /// اضافه کردن trace به store
    /// </summary>
    public static void AddTrace(object trace)
    {
        _traces.Enqueue(trace);
        
        // حذف trace‌های قدیمی اگر تعداد از حد مجاز بیشتر شد
        while (_traces.Count > MaxTraces)
        {
            _traces.TryDequeue(out _);
        }
    }

    /// <summary>
    /// دریافت trace‌های اخیر
    /// </summary>
    public static List<object> GetRecentTraces(int limit = 50, DateTime? from = null, string? serviceName = null, string? kind = null, int? status = null)
    {
        var traces = Enumerable.Reverse(_traces.ToArray()).ToList();
        
        // فیلتر بر اساس from
        if (from.HasValue)
        {
            traces = traces.Where(trace =>
            {
                var timestamp = GetTraceProperty(trace, "StartTime") ?? GetTraceProperty(trace, "Timestamp");
                if (timestamp is DateTime dt)
                    return dt >= from.Value;
                return true;
            }).ToList();
        }
        
        // فیلتر بر اساس serviceName
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            traces = traces.Where(trace =>
            {
                var service = GetTraceProperty(trace, "ServiceName")?.ToString();
                return service?.Equals(serviceName, StringComparison.OrdinalIgnoreCase) == true;
            }).ToList();
        }
        
        // فیلتر بر اساس kind
        if (!string.IsNullOrWhiteSpace(kind))
        {
            traces = traces.Where(trace =>
            {
                var traceKind = GetTraceProperty(trace, "Kind")?.ToString();
                return traceKind?.Equals(kind, StringComparison.OrdinalIgnoreCase) == true;
            }).ToList();
        }
        
        // فیلتر بر اساس status
        if (status.HasValue)
        {
            traces = traces.Where(trace =>
            {
                var traceStatus = GetTraceProperty(trace, "Status");
                if (traceStatus is int s)
                    return s == status.Value;
                return false;
            }).ToList();
        }
        
        return traces.Take(limit).ToList();
    }

    /// <summary>
    /// دریافت آمار trace‌ها
    /// </summary>
    public static object GetTraceStats()
    {
        var traces = _traces.ToArray();
        var totalTraces = traces.Length;
        var errorTraces = traces.Count(t => 
        {
            var status = GetTraceProperty(t, "Status");
            if (status is int s)
                return s == 2; // Error status
            return false;
        });
        
        var durations = traces
            .Select(t => GetTraceProperty(t, "DurationMs"))
            .Where(d => d is double || d is int || d is long)
            .Select(d => Convert.ToDouble(d))
            .Where(d => d > 0)
            .ToList();
        
        var avgDuration = durations.Any() ? durations.Average() : 0.0;
        var errorRate = totalTraces > 0 ? (errorTraces * 100.0 / totalTraces) : 0.0;
        
        return new
        {
            TotalTraces = totalTraces,
            ErrorTraces = errorTraces,
            AvgDurationMs = avgDuration,
            ErrorRate = errorRate
        };
    }

    // Helper methods
    private static int GetLogLevel(object log)
    {
        var level = GetLogProperty(log, "Level");
        if (level is int l) return l;
        if (level is string levelStr)
        {
            return levelStr.ToLower() switch
            {
                "trace" or "0" => 0,
                "debug" or "1" => 1,
                "information" or "info" or "2" => 2,
                "warning" or "3" => 3,
                "error" or "4" => 4,
                "critical" or "fatal" or "5" => 5,
                _ => 2
            };
        }
        return 2; // Default to Information
    }

    private static object? GetLogProperty(object log, string propertyName)
    {
        if (log == null) return null;
        
        var type = log.GetType();
        var prop = type.GetProperty(propertyName);
        if (prop != null)
            return prop.GetValue(log);
        
        // Try camelCase
        var camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        prop = type.GetProperty(camelCase);
        if (prop != null)
            return prop.GetValue(log);
        
        // Try dictionary access
        if (log is System.Collections.IDictionary dict && dict.Contains(propertyName))
            return dict[propertyName];
        
        return null;
    }

    private static object? GetTraceProperty(object trace, string propertyName)
    {
        if (trace == null) return null;
        
        var type = trace.GetType();
        var prop = type.GetProperty(propertyName);
        if (prop != null)
            return prop.GetValue(trace);
        
        // Try camelCase
        var camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
        prop = type.GetProperty(camelCase);
        if (prop != null)
            return prop.GetValue(trace);
        
        // Try dictionary access
        if (trace is System.Collections.IDictionary dict && dict.Contains(propertyName))
            return dict[propertyName];
        
        return null;
    }
}

