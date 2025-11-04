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
            eventContainer.ClearEvents();
            if (events != null)
            {
                foreach (var notification in events)
                    jobExecuter.Enqueue<IJobPublisher>(job => job.Run(notification, cancellationToken), "default");
            }
        }
        return response;
    }
}
