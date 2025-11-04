using Castle.DynamicProxy;

namespace Neo.Domain.Features.Telementry;

public static class TelemetryProxyFactory
{
    private static readonly ProxyGenerator _proxyGenerator = new();

    public static TService Create<TService>(TService impl, ITelementryBehaviour telemetry)
        where TService : class
    {
        return _proxyGenerator.CreateInterfaceProxyWithTargetInterface(
            impl, new TelemetryInterceptor(telemetry));
    }
}