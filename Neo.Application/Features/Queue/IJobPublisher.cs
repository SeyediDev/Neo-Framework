namespace Neo.Application.Features.Queue;

public interface IJobPublisher : IJob
{
    [Telemetry("Job", "JobPublisher")]
    Task Run<TEvent>(TEvent notification, CancellationToken cancellationToken)
        where TEvent : INotification;
}