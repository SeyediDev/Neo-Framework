using Castle.DynamicProxy;

namespace Neo.Domain.Features.Telementry;

public static class TelemetryProxyFactory
{
    private static readonly ProxyGenerator ProxyGenerator = new();

    public static TService Create<TService>(TService impl, ITelementryBehaviour telemetry) where TService : class
    {
        ArgumentNullException.ThrowIfNull(impl);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!typeof(TService).IsInterface)
            throw new ArgumentException("Telemetry proxies require an interface service type.", nameof(TService));
        return ProxyGenerator.CreateInterfaceProxyWithTarget<TService>(impl, new TelemetryInterceptor(telemetry));
    }
}
