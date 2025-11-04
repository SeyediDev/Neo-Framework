using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Diagnostics.Metrics;

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
}

public class TelementryObject : ITelementryObject
{
    public Meter Meter { get; }
    public ActivitySource ActivitySource { get; }
    public Counter<int> RequestCounter { get; }
    public Counter<int> RequestSuccessCounter { get; }
    public Counter<int> RequestFailureCounter { get; }
    public Histogram<double> RequestDuration { get; }
    public UpDownCounter<int> RequestInflights { get; }

    public TelementryObject(IOptions<TelemetryOptions> telemetryOptions)
    {
        Meter = new(telemetryOptions.Value.ApplicationName, telemetryOptions.Value.ApplicationVersion);
        ActivitySource = new(telemetryOptions.Value.ApplicationName, telemetryOptions.Value.ApplicationVersion);
        RequestCounter = Meter.CreateCounter<int>("request.total", null, "تعداد درخواست");
        RequestSuccessCounter = Meter.CreateCounter<int>("request.success", null, "تعداد درخواست موفق");
        RequestFailureCounter = Meter.CreateCounter<int>("request.failure", null, "تعداد درخواست شکست خورده");
        RequestDuration = Meter.CreateHistogram<double>("request.duration", "ms", "مدت زمان انجام درخواست");
        RequestInflights = Meter.CreateUpDownCounter<int>("request.inflights", null, "تعداد درخواست درحال انجام");
    }
}