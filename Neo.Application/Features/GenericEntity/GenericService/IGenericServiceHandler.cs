namespace Neo.Application.Features.GenericEntity.GenericService;

public interface IGenericServiceHandler
{
    Task<TResponse?> Handle<TRequest, TResponse>(TRequest request,
        Func<TRequest, CancellationToken, Task<TResponse?>> service, CancellationToken cancellationToken)
        where TRequest : notnull;
    Task<TResponse?> Handle<TRequest, TResponse>(TRequest request, string requestName,
        Func<TRequest, CancellationToken, Task<TResponse?>> service, CancellationToken cancellationToken)
        where TRequest : notnull;

    Task Handle<TRequest>(TRequest request,
        Func<TRequest, CancellationToken, Task> service, CancellationToken cancellationToken)
        where TRequest : notnull;
    Task Handle<TRequest>(TRequest request, string requestName,
        Func<TRequest, CancellationToken, Task> service, CancellationToken cancellationToken)
        where TRequest : notnull;
}
