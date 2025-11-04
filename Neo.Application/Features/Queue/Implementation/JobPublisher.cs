namespace Neo.Application.Features.Queue.Implementation;

public class JobPublisher(IPublisher publisher) : IJobPublisher
{
    public async Task Run<TEvent>(TEvent notification, CancellationToken cancellationToken)
        where TEvent : INotification
    {
        await publisher.Publish(notification, cancellationToken);
    }
}
