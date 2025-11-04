namespace Neo.Domain.Features.PubSub;

public interface ICandoPublisher
{
    Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class;
}
