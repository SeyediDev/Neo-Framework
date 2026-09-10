using MediatR;
using Neo.Domain.Repository;
using ProductCatalog.Domain;

namespace ProductCatalog.Application;

public sealed record GetProduct(Guid Id) : IRequest<ProductView?>;

public sealed class GetProductHandler(IQueryRepository<Product, Guid> repository)
    : IRequestHandler<GetProduct, ProductView?>
{
    public async Task<ProductView?> Handle(GetProduct request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.Id, cancellationToken);
        return product is null ? null : new ProductView(product.Id, product.Name, product.Price);
    }
}
