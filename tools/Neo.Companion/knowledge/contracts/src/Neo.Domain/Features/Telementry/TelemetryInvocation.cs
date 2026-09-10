using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Neo.Domain.Features.Telementry;

// Converts only the return wrapper, preserving the service method's declared return type.
internal static class TelemetryInvocation
{
    private static readonly MethodInfo TaskResultMethod = typeof(TelemetryInvocation)
        .GetMethod(nameof(HandleTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ValueTaskResultMethod = typeof(TelemetryInvocation)
        .GetMethod(nameof(HandleValueTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static TelemetryAttribute? FindAttribute(MethodInfo contract, MethodInfo implementation) =>
        implementation.GetCustomAttribute<TelemetryAttribute>(inherit: true)
        ?? contract.GetCustomAttribute<TelemetryAttribute>(inherit: true);

    internal static object? Invoke(ITelementryBehaviour telemetry, MethodInfo method,
        TelemetryAttribute attribute, object?[] arguments, Func<object?> proceed)
    {
        var parameters = method.GetParameters();
        var token = CancellationToken.None;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(CancellationToken) && arguments[i] is CancellationToken ct)
                token = ct;
            else if (arguments[i] is null && parameters[i].ParameterType == typeof(Meter))
                arguments[i] = telemetry.Meter;
            else if (arguments[i] is null && parameters[i].ParameterType == typeof(ActivitySource))
                arguments[i] = telemetry.ActivitySource;
        }
        // Empty/null requests still run; the original method arguments are never replaced.
        object request = arguments.Length > 0 && arguments[0] is not null
            ? arguments[0]! : new EmptyRequest(method.Name);
        var call = new Call(request, attribute.Component ?? method.DeclaringType?.Name ?? "service",
            attribute.ServiceName ?? method.Name, attribute.ActivityKind, token, method.Name, proceed);
        var returnType = method.ReturnType;
        if (returnType == typeof(Task)) return HandleTask(telemetry, call);
        if (returnType == typeof(ValueTask)) return new ValueTask(HandleValueTask(telemetry, call));
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return InvokeGeneric(TaskResultMethod, returnType.GetGenericArguments()[0], telemetry, call);
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return InvokeGeneric(ValueTaskResultMethod, returnType.GetGenericArguments()[0], telemetry, call);
        if (returnType == typeof(void))
        {
            telemetry.HandleRequest<object>((_, _) => { proceed(); return Task.CompletedTask; },
                call.Request, call.Component, call.Service, call.Kind, null, token, method.Name).GetAwaiter().GetResult();
            return null;
        }
        // A synchronous service remains synchronous; async return shapes never enter this branch.
        return telemetry.HandleRequestResponse<object, object>((_, _) => Task.FromResult(proceed()),
            call.Request, call.Component, call.Service, call.Kind, null, token, method.Name).GetAwaiter().GetResult();
    }

    private static Task HandleTask(ITelementryBehaviour telemetry, Call call) =>
        telemetry.HandleRequest<object>(async (_, _) => await ((Task)call.Proceed()!).ConfigureAwait(false),
            call.Request, call.Component, call.Service, call.Kind, null, call.Token, call.Caller);

    private static Task HandleValueTask(ITelementryBehaviour telemetry, Call call) =>
        telemetry.HandleRequest<object>(async (_, _) => await ((ValueTask)call.Proceed()!).ConfigureAwait(false),
            call.Request, call.Component, call.Service, call.Kind, null, call.Token, call.Caller);

    private static Task<T?> HandleTaskResult<T>(ITelementryBehaviour telemetry, Call call) =>
        telemetry.HandleRequestResponse<object, T>(async (_, _) => await ((Task<T>)call.Proceed()!).ConfigureAwait(false),
            call.Request, call.Component, call.Service, call.Kind, null, call.Token, call.Caller);

    private static ValueTask<T?> HandleValueTaskResult<T>(ITelementryBehaviour telemetry, Call call) =>
        new(telemetry.HandleRequestResponse<object, T>(async (_, _) => await ((ValueTask<T>)call.Proceed()!).ConfigureAwait(false),
            call.Request, call.Component, call.Service, call.Kind, null, call.Token, call.Caller));

    private static object? InvokeGeneric(MethodInfo helper, Type result, ITelementryBehaviour telemetry, Call call)
    {
        try { return helper.MakeGenericMethod(result).Invoke(null, [telemetry, call]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed record EmptyRequest(string Method);
    private sealed record Call(object Request, string Component, string Service, ActivityKind Kind,
        CancellationToken Token, string Caller, Func<object?> Proceed);
}
