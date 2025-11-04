using Neo.Common.Extensions;
using Castle.DynamicProxy;

namespace Neo.Domain.Features.Telementry;

public class TelemetryInterceptor(ITelementryBehaviour telemetry) : IInterceptor
{
    private readonly ITelementryBehaviour _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    public void Intercept(IInvocation invocation)
    {
        var method = invocation.MethodInvocationTarget ?? invocation.Method;

        // خواندن TelemetryAttribute از متد یا اینترفیس
        var telemetryAttr = method.GetMethodAttribute<TelemetryAttribute>(method.DeclaringType);
        if (telemetryAttr == null)
        {
            invocation.Proceed();
            return;
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            invocation.Proceed();
            return;
        }

        object? request = invocation.Arguments[0];
        CancellationToken? ct = parameters.FindParameter<CancellationToken>(invocation.Arguments!);

        Type requestType = request!.GetType();
        Type? responseType = method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;

        Func<object, CancellationToken, Task<object?>> next = async (r, c) =>
        {
            invocation.Arguments[0] = r;
            if (parameters.Length > 1 && ct != null)
                invocation.Arguments[1] = c;

            invocation.Proceed();

            if (invocation.ReturnValue is Task t)
            {
                await t.ConfigureAwait(false);
                if (responseType != null)
                    return t.GetType().GetProperty("Result")!.GetValue(t);
                return null;
            }

            return invocation.ReturnValue;
        };

        // انتخاب متد مناسب Handle
        Task? handleTask;
        if (responseType != null)
        {
            var handleMethod = typeof(ITelementryBehaviour)
                .GetMethod(nameof(ITelementryBehaviour.HandleRequestResponse))!
                .MakeGenericMethod(requestType, responseType);

            handleTask = (Task?)handleMethod.Invoke(_telemetry,
                [next, request,
                telemetryAttr.Component ?? method.DeclaringType?.Name ?? "",
                telemetryAttr.ServiceName ?? method.Name,
                telemetryAttr.ActivityKind, null, ct ?? CancellationToken.None])!;
        }
        else
        {
            var handleMethod = typeof(ITelementryBehaviour)
                .GetMethod(nameof(ITelementryBehaviour.HandleRequest))!
                .MakeGenericMethod(request.GetType());

            handleTask = (Task?)handleMethod.Invoke(_telemetry,
                [next, request,
                telemetryAttr.Component ?? method.DeclaringType?.Name ?? "",
                telemetryAttr.ServiceName ?? method.Name, 
                telemetryAttr.ActivityKind, null, ct ?? CancellationToken.None])!;
        }

        handleTask?.GetAwaiter().GetResult();
        invocation.ReturnValue = handleTask;
    }    
}
