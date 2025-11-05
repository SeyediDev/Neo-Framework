namespace Neo.Domain.Features.PubSub;

public interface INeoPublisher
{
    Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class;
}
