using System.Diagnostics;

namespace Neo.Domain.Features.Telementry;

[AttributeUsage(AttributeTargets.Method)]
public class TelemetryAttribute(
    string component = null!, string serviceName = null!, ActivityKind activityKind = ActivityKind.Internal
    ) : Attribute
{
    public string Component { get; } = component;
    public string ServiceName { get; } = serviceName;
    public ActivityKind ActivityKind { get; } = activityKind;
}
