using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Neo.Companion.TelemetryDemo;
using Neo.Domain.Features.Telementry;

namespace Neo.Companion.TelemetryDemo;

public static class DemoProgram
{
    public static async Task Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "manual";
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DemoServices.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Console.WriteLine($"span={activity.DisplayName}; status={activity.Status}")
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DemoServices.SourceName) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, state) =>
            Console.WriteLine($"metric={instrument.Name}; value={value}"));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            Console.WriteLine($"metric={instrument.Name}; value={value:F2}"));
        meterListener.Start();
        using var provider = DemoServices.Create(mode);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductLookup>();
        var result = await service.LookupAsync(new LookupRequest(Guid.NewGuid()), CancellationToken.None);
        Console.WriteLine($"mode={mode}; available={result?.Available}");
    }
}
