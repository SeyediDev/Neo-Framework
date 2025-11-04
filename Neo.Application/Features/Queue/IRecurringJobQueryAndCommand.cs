/*namespace Neo.Application.Features.Queue;

public interface IRecurringJobQueryAndCommand<TQueryResult, TCommandRequest> 
    where TCommandRequest : IRequest, new()
{
    [Telemetry]
    Task QueryAndCommand(string jobName, DateTime firstRunTime,
        Func<DateTime, Task<List<TQueryResult>>> query,
        Action<TCommandRequest, List<TQueryResult>> commandSetter);
}
*/