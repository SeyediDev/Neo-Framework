using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neo.Domain.Features.Telementry;
using Neo.Infrastructure.Features.Telementry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Neo.Companion.Tests;

[Collection("Telemetry")]
public sealed class TelemetryExportTests
{
    [Fact]
    public void Neo_export_registration_subscribes_custom_sources_and_binds_options()
    {
        var name = "Neo.ExportTests." + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ApplicationName"] = name, ["ApplicationVersion"] = "1.2.3" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeoOpenTelementry(config);
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        _ = provider.GetRequiredService<MeterProvider>();
        Assert.Equal(name, provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.ApplicationName);
        var resource = tracer.GetResource().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("1.2.3", resource["service.version"]);
        using var source = new ActivitySource(name);
        using var activity = source.StartActivity("export-test");
        Assert.NotNull(activity);
        using var meter = new Meter(name);
        Assert.True(meter.CreateCounter<int>("test-counter").Enabled);
    }
}
