using System.Reflection;

namespace Neo.Domain.Features.Telementry;

public static class TelemetryProxy<T> where T : class
{
    public static T Create(T decorated, ITelementryBehaviour telemetry)
    {
        ArgumentNullException.ThrowIfNull(decorated);
        ArgumentNullException.ThrowIfNull(telemetry);

        var proxy = DispatchProxy.Create<T, TelemetryDispatchProxy<T>>();
        ((TelemetryDispatchProxy<T>)(object)proxy).Init(decorated, telemetry);
        return proxy;
    }
}