using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Neo.Domain.Features.Telementry;

// Retained for consumers constructing this public DispatchProxy type directly.
// All entrypoints use the same typed invocation engine.
public class TelemetryDispatchProxy<T> : DispatchProxy where T : class
{
    private T? _proxy;

    public void Init(T decorated, ITelementryBehaviour telemetry) =>
        _proxy = TelemetryProxyFactory.Create(decorated, telemetry);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_proxy is null) throw new InvalidOperationException("Proxy is not initialized.");
        try { return targetMethod.Invoke(_proxy, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
