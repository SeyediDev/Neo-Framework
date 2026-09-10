using Castle.DynamicProxy;

namespace Neo.Domain.Features.Telementry;

public class TelemetryInterceptor(ITelementryBehaviour telemetry) : IInterceptor
{
    private readonly ITelementryBehaviour _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    public void Intercept(IInvocation invocation)
    {
        var implementation = invocation.MethodInvocationTarget ?? invocation.Method;
        var attribute = TelemetryInvocation.FindAttribute(invocation.Method, implementation);
        if (attribute is null)
        {
            invocation.Proceed();
            return;
        }
        var proceed = invocation.CaptureProceedInfo();
        invocation.ReturnValue = TelemetryInvocation.Invoke(_telemetry, invocation.Method, attribute,
            invocation.Arguments, () => { proceed.Invoke(); return invocation.ReturnValue; });
    }
}
