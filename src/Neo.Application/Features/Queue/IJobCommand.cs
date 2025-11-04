namespace Neo.Application.Features.Queue;

public interface IJobCommand : IJob
{
    [Telemetry("Job", "RunCommand")]
    Task Run<TCommandRequest>(TCommandRequest command, CancellationToken cancellationToken)
            where TCommandRequest : IRequest;
}