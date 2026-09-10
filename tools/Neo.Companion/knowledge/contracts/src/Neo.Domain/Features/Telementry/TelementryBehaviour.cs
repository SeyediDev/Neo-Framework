using Neo.Domain.Features.Client;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Diagnostics.Metrics;

namespace Neo.Domain.Features.Telementry;

public interface ITelementryBehaviour
{
    Task<TResponse?> HandleRequestResponse<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse?>> next,
        TRequest request,
        string component,
        string serviceName,
        ActivityKind activityKind,
        IEnumerable<KeyValuePair<string, object?>>? extraTags,
        CancellationToken cancellationToken,
        [CallerMemberName] string caller = "",
        [CallerLineNumber] int line = 0)
        where TRequest : notnull;

    Task HandleRequest<TRequest>(
        Func<TRequest, CancellationToken, Task> next,
        TRequest request,
        string component,
        string serviceName,
        ActivityKind activityKind,
        IEnumerable<KeyValuePair<string, object?>>? extraTags,
        CancellationToken cancellationToken,
        [CallerMemberName] string caller = "",
        [CallerLineNumber] int line = 0)
        where TRequest : notnull;
    Meter Meter { get; }
	ActivitySource ActivitySource { get; }
}

public class TelementryBehaviour(
    ITelementryObject telementry,
    IRequesterUser user
) : ITelementryBehaviour
{
    // A logical async call owns its state, including nested calls on the same scoped service.
    private readonly AsyncLocal<OperationContext?> _operation = new();
    private sealed record OperationContext(string Component, string Service, ActivityKind Kind,
        KeyValuePair<string, object?>[] Tags);

	public Meter Meter => telementry.Meter;
	public ActivitySource ActivitySource => telementry.ActivitySource;
	#region Handle Methods
    public Task<TResponse?> HandleRequestResponse<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse?>> next,
        TRequest request,
        string component,
        string serviceName,
        ActivityKind activityKind,
        IEnumerable<KeyValuePair<string, object?>>? extraTags,
        CancellationToken cancellationToken,
        [CallerMemberName] string caller = "",
        [CallerLineNumber] int line = 0)
        where TRequest : notnull
    {
        return ExecuteAsync(next, request, component, serviceName, activityKind, extraTags, caller, line, cancellationToken);
    }

    public Task HandleRequest<TRequest>(
        Func<TRequest, CancellationToken, Task> next,
        TRequest request,
        string component,
        string serviceName,
        ActivityKind activityKind,
        IEnumerable<KeyValuePair<string, object?>>? extraTags,
        CancellationToken cancellationToken,
        [CallerMemberName] string caller = "",
        [CallerLineNumber] int line = 0)
        where TRequest : notnull
    {
        return ExecuteAsync(async (r, ct) => { await next(r, ct); return true; }, request, component, serviceName, activityKind, extraTags, caller, line, cancellationToken);
    }

    #endregion

    #region Core Execution

    private async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(
        Func<TRequest, CancellationToken, Task<TResponse?>> next,
        TRequest request,
        string component,
        string serviceName,
        ActivityKind activityKind,
        IEnumerable<KeyValuePair<string, object?>>? extraTags,
        string caller,
        int line,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        TResponse? response = default;
        Exception? exception = null;
        var previous = _operation.Value;
        _operation.Value = new OperationContext(component, serviceName, activityKind,
            GetTags(component, serviceName, $"{caller}({line})", activityKind, extraTags));
        var timer = Stopwatch.StartNew();
        try
        {
            using var activity = OnInit(request, component, serviceName, caller, line, activityKind, extraTags);
            try
            {
                response = await next(request, cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch (Exception error)
            {
                exception = error;
                throw;
            }
            finally
            {
                OnFinalize(request, response, exception, timer, activity);
            }
        }
        finally { _operation.Value = previous; }
    }

    #endregion

    #region Hooks

    protected virtual Activity? OnInit<TRequest>(TRequest request, string component, string serviceName, string caller, int line, ActivityKind activityKind, IEnumerable<KeyValuePair<string, object?>>? extraTags)
    {
        var state = _operation.Value!;
        return telementry.StartActivity(request, component, serviceName, state.Tags, activityKind);
    }

    protected virtual void OnFinalize<TRequest, TResponse>(TRequest request, TResponse? response, Exception? ex, Stopwatch timer, Activity? activity)
    {
        var state = _operation.Value!;
        telementry.OnFinalize(request, response, state.Component, state.Service, activity,
            ex is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error, ex?.Message, timer, state.Tags, state.Kind);
    }

    #endregion

    #region Private Helpers

    private KeyValuePair<string, object?>[] GetTags(string component, string serviceName, string callerName,
        ActivityKind activityKind, IEnumerable<KeyValuePair<string, object?>>? extraTags)
    {
        Dictionary<string, object?> tagDict = new()
        {
            // بر اساس کلید یکتا اضافه می‌کنیم
            ["span.kind"] = activityKind.ToString(),
            ["span.name"] = $"{component}.{serviceName}",
            ["client.user.id"] = user?.Id?.ToString(),
            ["client.app.name"] = user?.AppName,
            ["client.correlation.id"] = user?.CorrelationId,
            ["caller"] = callerName
        };

        if (extraTags != null)
        {
            foreach (var tag in extraTags)
            {
                // فقط اگر کلید قبلاً اضافه نشده باشد
                if (!tagDict.ContainsKey(tag.Key))
                    tagDict[tag.Key] = tag.Value;
            }
        }

        // تبدیل دیکشنری به آرایه
        return [.. tagDict.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))];
    }

    #endregion
}
