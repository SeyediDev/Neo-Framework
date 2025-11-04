/*namespace Stts.Shared.Application.Jobs;

public interface IRecurringJobQueryAndEvent<TQueryRequest, TQueryResponse, TQueryEntity, TEvent> 
    where TQueryRequest : IRequest<TQueryResponse>, new()
    where TEvent : IEvent, new()
{
    [Telemetry]
    Task QueryAndEvent(string jobName, DateTime firstRunTime,
        Action<TQueryRequest> querySetter,
        Action<TEvent, TQueryResponse> eventSetter, CancellationToken cancellationToken);
}
public interface IRecurringJobQueryAndEvent<TQueryEntity, TEvent> 
    where TEvent : IEvent, new()
{
    [Telemetry]
    Task QueryAndEvent(string jobName, DateTime firstRunTime, int batchSize/*ziro or negative means all* /,
        Func<DateTime, Task<List<TQueryEntity>>> query,
        Action<TEvent, List<TQueryEntity>> eventSetter, CancellationToken cancellationToken);
}*/