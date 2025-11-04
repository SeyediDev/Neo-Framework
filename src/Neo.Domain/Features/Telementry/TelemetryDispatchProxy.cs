using Neo.Common.Extensions;
using System.Collections.Concurrent;
using System.Reflection;

namespace Neo.Domain.Features.Telementry;

public class TelemetryDispatchProxy<T> : DispatchProxy where T : class
{
    private T? _decorated;
    private ITelementryBehaviour? _telemetry;
    private readonly ConcurrentDictionary<MethodInfo, Delegate> _delegateCache = new();

    public void Init(T decorated, ITelementryBehaviour telemetry)
    {
        _decorated = decorated ?? throw new ArgumentNullException(nameof(decorated));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_decorated == null || _telemetry == null)
        {
            throw new InvalidOperationException("Proxy is not initialized.");
        }

        TelemetryAttribute? telemetryAttr = targetMethod.GetMethodAttribute<TelemetryAttribute>(_decorated.GetType());
        return telemetryAttr == null
            ? targetMethod.Invoke(_decorated, args)
            : InvokeWithTelemetryAsync(telemetryAttr, targetMethod, args!).GetAwaiter().GetResult();
    }

    private async Task<object?> InvokeWithTelemetryAsync(TelemetryAttribute telemetryAttr, MethodInfo targetMethod, object[] args)
    {
        ParameterInfo[] parameters = targetMethod.GetParameters();
        if (parameters.Length == 0)
        {
            throw new InvalidOperationException("Handle method requires at least one parameter.");
        }

        object request = args[0];
        CancellationToken ct = parameters.FindParameter<CancellationToken>(args);

        Type requestType = request!.GetType();
        Type? responseType = targetMethod.ReturnType.IsGenericType ? targetMethod.ReturnType.GetGenericArguments()[0] : null;

        // انتخاب متد مناسب از ITelementryBehaviour
        MethodInfo handleMethod = responseType != null
            ? typeof(ITelementryBehaviour).GetMethod(nameof(ITelementryBehaviour.HandleRequestResponse))!
                .MakeGenericMethod(requestType, responseType)
            : typeof(ITelementryBehaviour).GetMethod(nameof(ITelementryBehaviour.HandleRequest))!
                .MakeGenericMethod(requestType);

        // delegate next
        Delegate nextDelegate = GetOrAddDelegate(targetMethod, requestType, responseType);

        //TODO مطمئن نیستم این دو خط زیر کار کند
        string callerName = parameters.FindParameter<string>(args, "[CallerMemberName]") ?? targetMethod.Name;
        int lineNumber = parameters.FindParameter<int>(args, "[CallerLineNumber]");

        object? handleTask = handleMethod.Invoke(_telemetry,
        [
            nextDelegate, request,
            telemetryAttr.Component??targetMethod.DeclaringType?.Name ?? "",
            telemetryAttr.ServiceName??targetMethod.Name, telemetryAttr.ActivityKind, null, ct,
            callerName, lineNumber
        ]);

        if (handleTask is Task t)
        {
            await t.ConfigureAwait(false);
            return responseType != null ? t.GetType().GetProperty("Result")!.GetValue(t) : null;
        }

        return handleTask;
    }

    private Delegate GetOrAddDelegate(MethodInfo targetMethod, Type requestType, Type? responseType)
    {
        return _delegateCache.GetOrAdd(targetMethod, _ =>
        {
            if (responseType != null)
            {
                Type funcType = typeof(Func<,,>).MakeGenericType(requestType, typeof(CancellationToken), typeof(Task<>).MakeGenericType(responseType));
                return Delegate.CreateDelegate(funcType, _decorated!, targetMethod);
            }
            else
            {
                Type funcType = typeof(Func<,,>).MakeGenericType(requestType, typeof(CancellationToken), typeof(Task));
                return Delegate.CreateDelegate(funcType, _decorated!, targetMethod);
            }
        });
    }
}
