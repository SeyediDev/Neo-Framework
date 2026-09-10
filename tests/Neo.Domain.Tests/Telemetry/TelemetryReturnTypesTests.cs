using System.Diagnostics;
using System.Reflection;
using Neo.Domain.Features.Telementry;
using Xunit;
using Capture = Neo.Domain.Tests.Telemetry.TelemetryRuntimeTests.Capture;

namespace Neo.Domain.Tests.Telemetry;

public sealed class TelemetryReturnTypesTests
{
    private static IService Create(string factory, Service target, ITelementryBehaviour behaviour)
    {
        if (factory == "castle") return TelemetryProxyFactory.Create<IService>(target, behaviour);
        if (factory == "compatibility") return TelemetryProxy<IService>.Create(target, behaviour);
        var service = DispatchProxy.Create<IService, TelemetryDispatchProxy<IService>>();
        ((TelemetryDispatchProxy<IService>)(object)service).Init(target, behaviour);
        return service;
    }

    [Theory]
    [InlineData("castle")]
    [InlineData("compatibility")]
    [InlineData("dispatch")]
    public async Task Proxies_preserve_return_shapes_and_implementation_attributes(string factory)
    {
        using var capture = new Capture();
        var target = new Service();
        var service = Create(factory, target, capture.Behaviour);
        await service.NoArgumentsAsync();
        Assert.Equal(42, await service.ValueAsync(42));
        await service.ValueVoidAsync();
        Assert.Equal(7, service.Sync(7));
        service.Void();
        Assert.Equal("x", await service.GenericAsync("x", 100, default));
        Assert.Equal(12, await service.GenericAsync(12, 100, default));
        Assert.Equal("implementation", await service.ImplementationOnlyAsync());
        Assert.Equal("plain", await service.PlainAsync());
        Assert.Equal(8, capture.Calls.Count);
        Assert.Contains(capture.Calls, call => call.Name == "impl.attribute");
        Assert.All(capture.Calls, call => Assert.Equal(ActivityStatusCode.Ok, call.Status));
    }

    [Theory]
    [InlineData("castle")]
    [InlineData("dispatch")]
    public async Task Null_requests_and_third_argument_cancellation_are_preserved(string factory)
    {
        using var capture = new Capture();
        var target = new Service();
        var service = Create(factory, target, capture.Behaviour);
        Assert.Null(await service.NullableAsync(null, "keep-me", default));
        Assert.Equal("keep-me", target.LastMarker);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(capture.Calls).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.NullableAsync("x", "unchanged", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal("unchanged", target.LastMarker);
        Assert.Contains(capture.Calls, call => call.Status == ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData("castle")]
    [InlineData("dispatch")]
    public async Task Original_sync_and_async_exceptions_escape_without_reflection_wrappers(string factory)
    {
        using var capture = new Capture();
        var target = new Service();
        var service = Create(factory, target, capture.Behaviour);
        Assert.Same(target.Error, Assert.Throws<InvalidOperationException>(() => service.SyncFailure()));
        Assert.Same(target.Error, await Assert.ThrowsAsync<InvalidOperationException>(() => service.AsyncFailure()));
        Assert.All(capture.Calls, call => Assert.Equal(ActivityStatusCode.Error, call.Status));
    }

    [Theory]
    [InlineData("castle")]
    [InlineData("dispatch")]
    public async Task Async_calls_return_before_business_work_finishes(string factory)
    {
        using var capture = new Capture();
        var service = Create(factory, new Service(), capture.Behaviour);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(() => returned.SetResult(service.WaitAsync(gate.Task)));
        try
        {
            var operation = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            gate.SetResult();
            Assert.Equal("finished", await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { gate.TrySetResult(); await invocation; }
    }

    public interface IService
    {
        [Telemetry] Task NoArgumentsAsync();
        [Telemetry] ValueTask<int> ValueAsync(int value);
        [Telemetry] ValueTask ValueVoidAsync();
        [Telemetry] int Sync(int value);
        [Telemetry] void Void();
        [Telemetry] Task<T> GenericAsync<T>(T request, int marker, CancellationToken cancellationToken);
        Task<string> ImplementationOnlyAsync();
        Task<string> PlainAsync();
        [Telemetry] Task<string?> NullableAsync(string? request, string marker, CancellationToken cancellationToken);
        [Telemetry] int SyncFailure();
        [Telemetry] Task AsyncFailure();
        [Telemetry] Task<string> WaitAsync(Task gate);
    }

    public sealed class Service : IService
    {
        public string? LastMarker { get; private set; }
        public InvalidOperationException Error { get; } = new("business failure");
        public Task NoArgumentsAsync() => Task.CompletedTask;
        public ValueTask<int> ValueAsync(int value) => ValueTask.FromResult(value);
        public ValueTask ValueVoidAsync() => ValueTask.CompletedTask;
        public int Sync(int value) => value;
        public void Void() { }
        public Task<T> GenericAsync<T>(T request, int marker, CancellationToken cancellationToken) => Task.FromResult(request);
        [Telemetry("impl", "attribute")]
        public Task<string> ImplementationOnlyAsync() => Task.FromResult("implementation");
        public Task<string> PlainAsync() => Task.FromResult("plain");
        public Task<string?> NullableAsync(string? request, string marker, CancellationToken cancellationToken)
        {
            LastMarker = marker;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(request);
        }
        public int SyncFailure() => throw Error;
        public Task AsyncFailure() => Task.FromException(Error);
        public async Task<string> WaitAsync(Task gate) { await gate; return "finished"; }
    }
}
