using Neo.Domain.Features.Client;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

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
}

public class TelementryBehaviour(
    ILogger<TelementryBehaviour> logger,
    ITelementryObject telementry,
    IRequesterUser user
) : ITelementryBehaviour
{
    // ===== Fields for internal state =====
    private string? _component;
    private string? _serviceName;
    private ActivityKind _activityKind;
    private KeyValuePair<string, object?>[] _tags = [];
    private Stopwatch _timer = new();

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
        using var activity = OnInit(request, component, serviceName, caller, line, activityKind, extraTags);

        try
        {
            response = await next(request, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            OnFinalize(request, response, exception);
        }
    }

    #endregion

    #region Hooks

    protected virtual Activity? OnInit<TRequest>(TRequest request, string component, string serviceName, string caller, int line, ActivityKind activityKind, IEnumerable<KeyValuePair<string, object?>>? extraTags)
    {
        // ===== Init internal state =====
        _component = component;
        _serviceName = serviceName;
        _activityKind = activityKind;
        _tags = GetTags(component, serviceName, $"{caller}({line})", activityKind, extraTags);
        _timer.Restart();

        var activity = telementry.ActivitySource.StartActivity(_serviceName!, _activityKind, Activity.Current?.Context ?? default, _tags);

        if (activity != null && user != null)
        {
            activity.AddBaggage("client.user.id", user.Id.ToString());
            activity.AddBaggage("client.app.name", user.AppName ?? "unknown");
            activity.AddBaggage("client.correlation.id", user.CorrelationId ?? "");
        }

        telementry.RequestInflights.Add(1, _tags);
        telementry.RequestCounter.Add(1, _tags);

        logger.LogInformation("Telemetry Init : Component={Component}, Service={Service}, Request={@Request}, ActivityKind={ActivityKind}, Tags={@Tags}",
            _component, _serviceName, request, _activityKind, _tags);
        return activity;
    }

    protected virtual void OnFinalize<TRequest, TResponse>(TRequest request, TResponse? response, Exception? ex)
    {
        _timer.Stop();
        double elapsedMs = _timer.Elapsed.TotalMilliseconds;
        logger.LogInformation(
            "Request finalized: Component={Component}, Service={Service}, Request={@Request}, Response={@Response}, ActivityKind={ActivityKind}, Tags={@Tags}, Duration={Duration}ms, Error={Error}, ExceptionMessage={Message}",
            _component, _serviceName, request, response, _activityKind, _tags, elapsedMs, ex!=null, ex?.Message);

        telementry.RequestDuration.Record(elapsedMs, _tags);

        if (response != null)
            telementry.RequestSuccessCounter.Add(1, _tags);
        else
            telementry.RequestFailureCounter.Add(1, _tags);

        telementry.RequestInflights.Add(-1, _tags);
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
            ["client.user.id"] = user?.Id,
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
