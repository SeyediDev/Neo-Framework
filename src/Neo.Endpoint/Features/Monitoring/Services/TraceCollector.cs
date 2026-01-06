using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Background service that collects traces using ActivityListener
/// </summary>
public class TraceCollector : BackgroundService
{
    private readonly ITraceStore _store;
    private readonly ILogger<TraceCollector> _logger;
    private readonly MonitoringStorageOptions _options;
    private ActivityListener? _activityListener;

    public TraceCollector(
        ITraceStore store,
        IOptions<MonitoringStorageOptions> options,
        ILogger<TraceCollector> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TraceCollector starting...");

        _activityListener = new ActivityListener
        {
            // Listen to all activities
            ShouldListenTo = _ => true,
            
            // Sample all activities
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            
            // Record when activity stops
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_activityListener);

        _logger.LogInformation("TraceCollector started. Listening for activities...");

        return Task.CompletedTask;
    }

    private void OnActivityStopped(Activity activity)
    {
        try
        {
            var span = ConvertToSpan(activity);
            _store.RecordSpan(span);
            
            _logger.LogTrace("Recorded span: {OperationName} ({TraceId}/{SpanId})", 
                span.OperationName, span.TraceId, span.SpanId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error recording activity {OperationName}", activity.OperationName);
        }
    }

    private static TraceSpan ConvertToSpan(Activity activity)
    {
        var tags = new Dictionary<string, string>();
        foreach (var tag in activity.Tags)
        {
            tags[tag.Key] = tag.Value ?? "";
        }

        var baggage = new Dictionary<string, string>();
        foreach (var item in activity.Baggage)
        {
            baggage[item.Key] = item.Value ?? "";
        }

        var events = activity.Events.Select(e => new SpanEvent
        {
            Name = e.Name,
            Timestamp = e.Timestamp.UtcDateTime,
            Attributes = e.Tags.ToDictionary(t => t.Key, t => t.Value?.ToString() ?? "")
        }).ToList();

        var status = activity.Status switch
        {
            ActivityStatusCode.Ok => SpanStatus.Ok,
            ActivityStatusCode.Error => SpanStatus.Error,
            _ => SpanStatus.Unset
        };

        return new TraceSpan
        {
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            ParentSpanId = activity.ParentSpanId.ToString(),
            OperationName = activity.OperationName,
            ServiceName = activity.Source.Name,
            StartTime = activity.StartTimeUtc,
            EndTime = activity.StartTimeUtc + activity.Duration,
            Status = status,
            StatusMessage = activity.StatusDescription,
            Kind = (SpanKind)activity.Kind,
            Tags = tags,
            Baggage = baggage,
            Events = events
        };
    }

    public override void Dispose()
    {
        _activityListener?.Dispose();
        base.Dispose();
    }
}

