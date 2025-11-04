namespace Neo.Application.Behaviours.MediatR;

public class MediatRTelementryBehaviour<TRequest, TResponse>(ITelementryBehaviour telementryBehaviour) 
    : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return (await telementryBehaviour.HandleRequestResponse(
            async (request, cancellationToken) =>
            {
                var response = await next(cancellationToken);
                return response;
            },
            request, nameof(TelemetryAttributeValue.cqrs), typeof(TRequest).Name, 
            System.Diagnostics.ActivityKind.Internal, null,
            cancellationToken))!;
    }
}
