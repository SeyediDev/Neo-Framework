using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Telementry;
using Xunit;

namespace Neo.Domain.Tests.Telemetry;

public sealed class TelemetryRuntimeTests
{
    [Fact]
    public async Task Proxy_preserves_Task_result_and_interface_attribute()
    {
        using var capture = new Capture();
        var service = TelemetryProxyFactory.Create<IProbe>(new Probe(), capture.Behaviour);
        Assert.Equal("hello!", await service.ReadAsync("hello", "!", CancellationToken.None));
        Assert.Equal("probe.read", Assert.Single(capture.Calls).Name);
    }

    [Fact]
    public async Task Registration_activates_telemetry_and_preserves_scope()
    {
        using var capture = new Capture();
        var services = new ServiceCollection();
        services.AddSingleton<ITelementryBehaviour>(capture.Behaviour);
        services.AddScopedWithTelemetry<IProbe, Probe>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProbe>();
        Assert.Same(service, scope.ServiceProvider.GetRequiredService<IProbe>());
        Assert.Equal("value!", await service.ReadAsync("value", "!", default));
        Assert.Single(capture.Calls);
    }

    [Fact]
    public async Task Timer_measures_work_and_successful_null_is_not_failure()
    {
        using var capture = new Capture();
        await capture.Behaviour.HandleRequestResponse<string, string>(async (_, _) =>
            { await Task.Delay(25); return null; }, "request", "probe", "null", ActivityKind.Internal, null, default);
        var call = Assert.Single(capture.Calls);
        Assert.True(call.Duration >= 10, $"Measured only {call.Duration} ms.");
        Assert.Equal(ActivityStatusCode.Ok, call.Status);
    }

    [Fact]
    public async Task Overlapping_operations_keep_their_own_tags()
    {
        using var capture = new Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = capture.Behaviour.HandleRequestResponse<string, string>(async (request, _) =>
            { entered.SetResult(); await release.Task; return request; }, "first", "A", "first", ActivityKind.Client, [new("owner", "A")], default);
        await entered.Task;
        await capture.Behaviour.HandleRequestResponse<string, string>((request, _) => Task.FromResult<string?>(request),
            "second", "B", "second", ActivityKind.Internal, [new("owner", "B")], default);
        release.SetResult(); await first;
        Assert.Contains(capture.Calls, c => c.Name == "A.first" && c.Kind == ActivityKind.Client && Equals(c.Tags["owner"], "A"));
        Assert.Contains(capture.Calls, c => c.Name == "B.second" && c.Kind == ActivityKind.Internal && Equals(c.Tags["owner"], "B"));
    }

    public interface IProbe
    {
        [Telemetry("probe", "read")]
        Task<string?> ReadAsync(string? request, string suffix, CancellationToken cancellationToken);
    }
    public sealed class Probe : IProbe
    {
        public async Task<string?> ReadAsync(string? request, string suffix, CancellationToken cancellationToken)
        { await Task.Delay(10, cancellationToken); return request + suffix; }
    }

    public sealed record Call(string Name, double Duration, ActivityStatusCode Status, ActivityKind Kind, Dictionary<string, object?> Tags);
    public sealed class Capture : ITelementryObject, IDisposable
    {
        public ConcurrentQueue<Call> Calls { get; } = new();
        public ITelementryBehaviour Behaviour { get; }
        public Meter Meter { get; } = new("Neo.TelemetryTests");
        public ActivitySource ActivitySource { get; } = new("Neo.TelemetryTests");
        public Counter<int> RequestCounter { get; }
        public Counter<int> RequestSuccessCounter { get; }
        public Counter<int> RequestFailureCounter { get; }
        public Histogram<double> RequestDuration { get; }
        public UpDownCounter<int> RequestInflights { get; }
        public Capture()
        {
            RequestCounter = Meter.CreateCounter<int>("requests");
            RequestSuccessCounter = Meter.CreateCounter<int>("success");
            RequestFailureCounter = Meter.CreateCounter<int>("failure");
            RequestDuration = Meter.CreateHistogram<double>("duration");
            RequestInflights = Meter.CreateUpDownCounter<int>("inflight");
            Behaviour = new TelementryBehaviour(this, null!);
        }
        public Activity? StartActivity<TRequest>(TRequest request, string component, string service,
            IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal) => null;
        public void OnFinalize<TRequest,TResponse>(TRequest request, TResponse? response, string component, string service,
            Activity? activity, ActivityStatusCode status, string? statusMessage, Stopwatch timer,
            IEnumerable<KeyValuePair<string, object?>>? tags = null, ActivityKind activityKind = ActivityKind.Internal)
        {
            timer.Stop();
            Calls.Enqueue(new Call($"{component}.{service}", timer.Elapsed.TotalMilliseconds, status, activityKind,
                (tags ?? []).ToDictionary(x => x.Key, x => x.Value)));
        }
        public void Dispose() { Meter.Dispose(); ActivitySource.Dispose(); }
    }
}
