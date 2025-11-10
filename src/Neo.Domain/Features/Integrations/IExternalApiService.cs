using Neo.Domain.Entities.Integrations;

namespace Neo.Domain.Features.Integrations;

public interface IExternalApiService
{
    Task<ExternalApiInvocationResult> InvokeAsync(ExternalApi externalApi, ExternalApiRequest request, CancellationToken cancellationToken = default);
}
