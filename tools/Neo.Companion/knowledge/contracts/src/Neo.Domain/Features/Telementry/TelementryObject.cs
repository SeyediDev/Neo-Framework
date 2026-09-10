using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo.Domain.Features.Client;

namespace Neo.Domain.Features.Telementry;

public interface ITelementryObject
{
    Meter Meter { get; }
    ActivitySource ActivitySource { get; }
    public Counter<int> RequestCounter { get; }
    public Counter<int> RequestSuccessCounter { get; }
    public Counter<int> RequestFailureCounter { get; }
    public Histogram<double> RequestDuration { get; }
    public UpDownCounter<int> RequestInflights { get; }
    public Activity? StartActivity<TRequest>(TRequest request,
		string component, string service,
		IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal);
	void OnFinalize<TRequest, TResponse>(TRequest request, TResponse? response,
		string component, string service, Activity? activity,
		ActivityStatusCode status, string? statusMessage,
		Stopwatch timer, IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal);
}

public class TelementryObject : ITelementryObject, IDisposable
{
    public Meter Meter { get; }
    public ActivitySource ActivitySource { get; }
    public Counter<int> RequestCounter { get; }
    public Counter<int> RequestSuccessCounter { get; }
    public Counter<int> RequestFailureCounter { get; }
    public Histogram<double> RequestDuration { get; }
    public UpDownCounter<int> RequestInflights { get; }
    private readonly IRequesterUser _user;
    private readonly ILogger<TelementryObject> _logger;


	public TelementryObject(IOptions<TelemetryOptions> telemetryOptions, IRequesterUser user, ILogger<TelementryObject> logger)
    {
        _user = user;
		_logger = logger;
		Meter = new(telemetryOptions.Value.ApplicationName, telemetryOptions.Value.ApplicationVersion);
        ActivitySource = new(telemetryOptions.Value.ApplicationName, telemetryOptions.Value.ApplicationVersion);
        RequestCounter = Meter.CreateCounter<int>("request.total", null, "تعداد درخواست");
        RequestSuccessCounter = Meter.CreateCounter<int>("request.success", null, "تعداد درخواست موفق");
        RequestFailureCounter = Meter.CreateCounter<int>("request.failure", null, "تعداد درخواست شکست خورده");
        RequestDuration = Meter.CreateHistogram<double>("request.duration", "ms", "مدت زمان انجام درخواست");
        RequestInflights = Meter.CreateUpDownCounter<int>("request.inflights", null, "تعداد درخواست درحال انجام");
    }

    public Activity? StartActivity<TRequest>(TRequest request,
		string component, string service,
		IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal)
    {
        var activity = ActivitySource.StartActivity($"{component}.{service}", activityKind, Activity.Current?.Context ?? default, tags);

        if (activity != null && _user != null)
        {
            activity.AddBaggage("client.user.id", _user.Id.ToString());
            activity.AddBaggage("client.app.name", _user.AppName ?? "unknown");
            activity.AddBaggage("client.correlation.id", _user.CorrelationId ?? "");
        }

		TagList metricTags = tags is null ? default : [.. tags];
		RequestInflights.Add(1, metricTags);
        RequestCounter.Add(1, metricTags);
		_logger.LogInformation("Telemetry Init : Component={Component}, Service={Service}, Request={@Request}, ActivityKind={ActivityKind}, Tags={@Tags}",
			component, service, request, activityKind, tags);
		return activity;
    }
	
	public void OnFinalize<TRequest, TResponse>(TRequest request, TResponse? response, 
		string component, string service, Activity? activity,
		ActivityStatusCode status, string? statusMessage, 
		Stopwatch timer, IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal)
	{
		timer.Stop();
		double elapsedMs = timer.Elapsed.TotalMilliseconds;
        TagList metricTags = tags is null ? default : [.. tags];
		RequestDuration.Record(elapsedMs, metricTags);

		// Record success or failure based on exception, not just response
		// If exception occurred, it's a failure regardless of response
		activity?.SetStatus(status, statusMessage);
		if (status == ActivityStatusCode.Error)
		{
			RequestFailureCounter.Add(1, metricTags);
			_logger.LogWarning(
				"Request finalized: Component={Component}, Service={Service}, " +
				"Request={@Request}, Response={@Response}, ActivityKind={ActivityKind}, " +
				"Tags={@Tags}, Duration={Duration}ms, Status={Status}, StatusMessage={StatusMessage}",
				component, service,
				request, response, activityKind,
				tags, elapsedMs, status, statusMessage);
		}
		else
		{
			RequestSuccessCounter.Add(1, metricTags);
			_logger.LogInformation(
				"Request finalized: Component={Component}, Service={Service}, " +
				"Request={@Request}, Response={@Response}, ActivityKind={ActivityKind}, " +
				"Tags={@Tags}, Duration={Duration}ms, Status={Status}, StatusMessage={StatusMessage}",
				component, service,
				request, response, activityKind,
				tags, elapsedMs, status, statusMessage);
		}

		RequestInflights.Add(-1, metricTags);
	}
    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
        GC.SuppressFinalize(this);
    }
}