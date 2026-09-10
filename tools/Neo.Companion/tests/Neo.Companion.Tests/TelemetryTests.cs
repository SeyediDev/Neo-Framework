using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Neo.Companion.TelemetryDemo;
using Neo.Domain.Features.Telementry;
using Xunit;

namespace Neo.Companion.Tests;

[Collection("Telemetry")]
public sealed class TelemetryTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("attribute")]
    public async Task Recipe_produces_a_success_span_and_preserves_response(string mode)
    {
        using var fixture = new TelemetryFixture(mode);
        var request = new LookupRequest(Guid.NewGuid());
        var response = await fixture.Scope.ServiceProvider.GetRequiredService<IProductLookup>().LookupAsync(request, CancellationToken.None);
        Assert.NotNull(response);
        Assert.Equal(request.ProductId, response.ProductId);
        Assert.True(response.Available);
        var activity = Assert.Single(fixture.Stopped);
        Assert.Equal("catalog.lookup", activity.DisplayName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public async Task Explicit_behaviour_preserves_exception_and_marks_error()
    {
        using var fixture = new TelemetryFixture("manual");
        var expected = new InvalidOperationException("demo failure");
        var behaviour = fixture.Scope.ServiceProvider.GetRequiredService<ITelementryBehaviour>();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behaviour.HandleRequestResponse<LookupRequest, LookupResult>((request, ct) => Task.FromException<LookupResult?>(expected),
                new LookupRequest(Guid.NewGuid()), "catalog", "failure", ActivityKind.Internal, null, CancellationToken.None));
        Assert.Same(expected, actual);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(fixture.Stopped).Status);
    }

    [Fact]
    public async Task Explicit_behaviour_forwards_cancellation()
    {
        using var fixture = new TelemetryFixture("manual");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = fixture.Scope.ServiceProvider.GetRequiredService<IProductLookup>();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LookupAsync(new LookupRequest(Guid.NewGuid()), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(fixture.Stopped).Status);
    }

    [Fact]
    public async Task AddScopedWithTelemetry_activates_the_attribute()
    {
        using var fixture = new TelemetryFixture("manual");
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Scope.ServiceProvider.GetRequiredService<ITelementryBehaviour>());
        services.AddScopedWithTelemetry<IProductLookup, ProductLookup>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProductLookup>();
        Assert.IsNotType<ProductLookup>(service);
        await service.LookupAsync(new LookupRequest(Guid.NewGuid()), CancellationToken.None);
        Assert.Single(fixture.Stopped);
    }

    private sealed class TelemetryFixture : IDisposable
    {
        public List<Activity> Stopped { get; } = [];
        private readonly ActivityListener listener;
        private readonly ServiceProvider provider;
        public IServiceScope Scope { get; }
        public TelemetryFixture(string mode)
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DemoServices.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Add(activity)
            };
            ActivitySource.AddActivityListener(listener);
            provider = DemoServices.Create(mode);
            Scope = provider.CreateScope();
        }
        public void Dispose()
        {
            var telemetry = Scope.ServiceProvider.GetRequiredService<ITelementryObject>();
            telemetry.ActivitySource.Dispose();
            telemetry.Meter.Dispose();
            Scope.Dispose(); provider.Dispose(); listener.Dispose();
        }
    }
}

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;
