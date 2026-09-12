using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.Domain;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Telementry;
using Neo.Companion.TelemetryDemo;

namespace Neo.Companion.DoctorCases.Healthy;

public interface IOrders
{
    [Telemetry("orders", "lookup")]
    Task<string> Lookup();
}
public sealed class Orders : IOrders
{
    public Task<string> Lookup() => Task.FromResult("found");
}
public static class Setup
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeoDomainServices(new ConfigurationBuilder().Build());
        services.Configure<TelemetryOptions>(options => options.ApplicationName = "Neo.Doctor.Healthy");
        services.AddScoped<IRequesterUser, DemoRequester>();
        services.AddScopedWithTelemetry<IOrders, Orders>();
        return services.BuildServiceProvider();
    }

    public static ActivityListener Listen(Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Neo.Doctor.Healthy",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
