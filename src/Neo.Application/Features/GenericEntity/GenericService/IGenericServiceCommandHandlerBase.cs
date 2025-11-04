namespace Neo.Application.Features.GenericEntity.GenericService;

public interface IGenericServiceCommandHandlerBase<TRequest, TResponse>
        where TRequest : notnull
{
    Task<TResponse?> Send(TRequest request, CancellationToken cancellationToken);
}

public abstract class GenericServiceCommandHandlerBase<TRequest, TResponse>(IGenericServiceHandler genericService)
    : IGenericServiceCommandHandlerBase<TRequest, TResponse>
        where TRequest : notnull
{
    protected abstract Task<TResponse?> Handle(TRequest request, CancellationToken cancellationToken);

    public async Task<TResponse?> Send(TRequest request, CancellationToken cancellationToken)
    {
        return await genericService.Handle(request, Handle, cancellationToken);
    }
}