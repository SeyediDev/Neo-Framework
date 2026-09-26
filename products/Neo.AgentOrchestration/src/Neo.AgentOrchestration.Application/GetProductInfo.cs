using MediatR;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Application;

public sealed record GetProductInfo : IRequest<ProductInfo>;

public sealed class GetProductInfoHandler : IRequestHandler<GetProductInfo, ProductInfo>
{
    public Task<ProductInfo> Handle(GetProductInfo request, CancellationToken cancellationToken)
        => Task.FromResult(new ProductInfo("Neo Agent Orchestration", "Foundation", true, true));
}
