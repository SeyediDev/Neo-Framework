using Neo.Application.Features.Queue;

namespace Neo.Application.Features.Outbox;

public interface IOutboxExecutionJob : IJob
{
    Task Run(long outboxId, CancellationToken cancellationToken);
}
