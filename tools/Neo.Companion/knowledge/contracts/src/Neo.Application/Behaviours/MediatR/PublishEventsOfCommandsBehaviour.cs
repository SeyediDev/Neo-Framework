using Neo.Application.Features.Queue;

namespace Neo.Application.Behaviours.MediatR;

public class PublishEventsOfCommandsBehaviour<TRequest, TResponse>(IJobExecuter jobExecuter)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        TResponse response = await next(cancellationToken);

        if (response is IEventContainer eventContainer)
        {
            var events = eventContainer.Events.ToList();
            foreach (var notification in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var jobId = await jobExecuter.EnqueueAsync<IJobPublisher>((job, ct) => job.Run(notification, ct), "default", cancellationToken);
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("Event was not queued. Configure a working job executor or use a transactional outbox.");
                eventContainer.Events.Remove(notification);
            }
        }
        return response;
    }
}
