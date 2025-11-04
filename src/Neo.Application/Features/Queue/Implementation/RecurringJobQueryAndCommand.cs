/*namespace Neo.Application.Jobs;;

public class RecurringJobQueryAndCommand<TQueryResult, TCommandRequest>(
    IRecurringJobLastExecutionTime recurringJobLastExecutionTime, IJobCommand<TCommandRequest> jobCommand,
    ILogger<RecurringJobQueryAndCommand<TQueryResult, TCommandRequest>> logger, IMetric metric)
    : IRecurringJobQueryAndCommand<TQueryResult, TCommandRequest>
    where TCommandRequest : IBaseCQRSRequest, new()
{
    public async Task QueryAndCommand(string jobName, DateTime firstRunTime,
            Func<DateTime, TraceabilityParams, Task<List<TQueryResult>>> query,
            Action<TCommandRequest, List<TQueryResult>> commandSetter)
    {
        TraceabilityParams trace = new(jobName);
        var lastExecutionTime = recurringJobLastExecutionTime.Get(jobName) ?? firstRunTime;
        logger.LogInformation("{trace} {lastExecutionTime}", trace, lastExecutionTime);

        List<TQueryResult> queryResult = await query(lastExecutionTime, trace);

        recurringJobLastExecutionTime.Set(jobName, DateTime.Now);
        logger.LogInformation("{trace} {lastExecutionTime} {Count}", trace, lastExecutionTime, queryResult?.Count ?? 0);
        metric.AddItem(jobName, queryResult?.Count ?? 0, MetricAggregation.Int, jobName);

        if ((queryResult?.Count ?? 0) == 0)
            return;

        TCommandRequest commandRequest = new()
        {
            TraceabilityParams = trace
        };
        commandSetter(commandRequest, queryResult);
        await jobCommand.Run(commandRequest, CancellationToken.None);
    }
}*/