namespace Neo.Application.Features.GenericEntity.GenericService;

public class GenericServiceHandler(ITelementryBehaviour telementryBehaviour)
    : IGenericServiceHandler
{
    public async Task<TResponse?> Handle<TRequest, TResponse>(TRequest request,
        Func<TRequest, CancellationToken, Task<TResponse?>> service, CancellationToken cancellationToken)
        where TRequest : notnull
    {
        return await Handle(request, typeof(TRequest).Name, service, cancellationToken);
    }

    public async Task<TResponse?> Handle<TRequest, TResponse>(TRequest request, string requestName,
        Func<TRequest, CancellationToken, Task<TResponse?>> service, CancellationToken cancellationToken)
        where TRequest : notnull
    {
        TResponse? response = await telementryBehaviour.HandleRequestResponse(
            async (request, cancellationToken) => await service(request, cancellationToken),
            request, nameof(TelemetryAttributeValue.domain_service), 
            requestName, System.Diagnostics.ActivityKind.Internal, null, cancellationToken);
        return response;
    }

    public async Task Handle<TRequest>(TRequest request, 
        Func<TRequest, CancellationToken, Task> service, CancellationToken cancellationToken) 
        where TRequest : notnull
    {
        await Handle(request, typeof(TRequest).Name, service, cancellationToken);
    }

    public async Task Handle<TRequest>(TRequest request, string requestName, 
        Func<TRequest, CancellationToken, Task> service, CancellationToken cancellationToken) 
        where TRequest : notnull
    {
        await telementryBehaviour.HandleRequest(
            async (request, cancellationToken) => await service(request, cancellationToken),
            request, nameof(TelemetryAttributeValue.domain_service), 
            requestName, System.Diagnostics.ActivityKind.Internal, null, cancellationToken);
    }
}