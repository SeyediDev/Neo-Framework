/*namespace Neo.Application.Jobs;;

public class RecurringJobQueryAndEvent<TQueryRequest, TQueryResponse, TQueryEntity, TEvent>(
    IRecurringJobLastExecutionTime recurringJobLastExecutionTime,
    ILogger<RecurringJobQueryAndEvent<TQueryRequest, TQueryResponse, TQueryEntity, TEvent>> logger, IMetric metric, IMediator mediator, IJobExecuter jobExecuter)
    : IRecurringJobQueryAndEvent<TQueryRequest, TQueryResponse, TQueryEntity, TEvent>
    where TQueryRequest : IRequest<TQueryResponse>, new()
    where TEvent : IEvent, new()
{
    public async Task QueryAndEvent(string jobName, DateTime firstRunTime,
        Action<TQueryRequest> querySetter,
        Action<TEvent, TQueryResponse> eventSetter, CancellationToken cancellationToken)
    {
        var lastExecutionTime = recurringJobLastExecutionTime.Get(jobName) ?? firstRunTime;
        logger.LogInformation("{lastExecutionTime}", lastExecutionTime);

        TQueryRequest request = new()
        {
            FromDate = lastExecutionTime,
            TraceabilityParams = trace,
        };
        querySetter(request);
        TQueryResponse response = await mediator.Send(request, cancellationToken);

        recurringJobLastExecutionTime.Set(jobName, DateTime.Now);
        logger.LogInformation("{trace} {lastExecutionTime} {response}", trace, lastExecutionTime, response);
        metric.AddItem(jobName, response?.Result?.Count ?? 0, MetricAggregation.Int, jobName);

        if ((response?.Result?.Count ?? 0) == 0)
            return;

        TEvent @event = new()
        {
            TraceabilityParams = trace
        };
        eventSetter(@event, response);

        jobExecuter.Enqueue<IJobPublish<IEvent>>(job => job.Run(@event, cancellationToken));
    }
}
public class RecurringJobQueryAndEvent<TQueryResult, TEvent>(
    IRecurringJobLastExecutionTime recurringJobLastExecutionTime,
    ILogger<RecurringJobQueryAndEvent<TQueryResult, TEvent>> logger,
    IMetric metric, ISendEvent<TQueryResult, TEvent> eventSender)
    : IRecurringJobQueryAndEvent<TQueryResult, TEvent>
    where TEvent : EntityChangedEvent, IEvent, new()
{
    public async Task QueryAndEvent(string jobName, DateTime firstRunTime, int batchSize/*ziro or negative means all* /,
            Func<DateTime, TraceabilityParams, Task<List<TQueryResult>>> query,
            Action<TEvent, List<TQueryResult>> eventSetter, CancellationToken cancellationToken)
    {
        TraceabilityParams trace = new(jobName);
        var lastExecutionTime = recurringJobLastExecutionTime.Get(jobName) ?? firstRunTime;
        logger.LogInformation("RecurringJobQueryAndEvent {trace} {dateTime}", trace, lastExecutionTime);

        List<TQueryResult> queryResult = await query(lastExecutionTime, trace);

        recurringJobLastExecutionTime.Set(jobName, DateTime.Now);
        logger.LogInformation("RecurringJobQueryAndEvent {trace} {dateTime} {Count}", trace, lastExecutionTime, queryResult?.Count ?? 0);
        metric.AddItem(jobName, queryResult?.Count ?? 0, MetricAggregation.Int, jobName);

        if ((queryResult?.Count ?? 0) == 0)
            return;

        eventSender.Send(batchSize, eventSetter, trace, lastExecutionTime, queryResult, cancellationToken);
    }
}
*/