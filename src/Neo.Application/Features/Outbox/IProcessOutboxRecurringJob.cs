using Neo.Application.Features.Queue;

namespace Neo.Application.Features.Outbox;

[RecurringJob(
    cronJob: "0 */2 * * * *", 
    queue: "outbox"
)]
public interface IProcessOutboxRecurringJob : IRecurringJob
{
}