namespace Neo.Domain.Features.Telementry;

public static class TelemetryProxy<T> where T : class
{
    public static T Create(T decorated, ITelementryBehaviour telemetry) =>
        TelemetryProxyFactory.Create(decorated, telemetry);
}
