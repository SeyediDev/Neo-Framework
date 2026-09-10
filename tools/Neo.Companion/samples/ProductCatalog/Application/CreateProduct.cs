using FluentValidation;
using MediatR;
using Neo.Domain.Repository;
using ProductCatalog.Domain;

namespace ProductCatalog.Application;

public sealed record CreateProduct(string? Name, decimal Price) : IRequest<ProductView>;
public sealed record ProductView(Guid Id, string Name, decimal Price);

public sealed class CreateProductValidator : AbstractValidator<CreateProduct>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Name).Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("Name is required.");
        RuleFor(x => x.Name).Must(name => name is null || name.Trim().Length <= Product.MaxNameLength)
            .WithMessage($"Name must be at most {Product.MaxNameLength} characters.");
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0);
    }
}

public sealed class CreateProductHandler(
    ICommandRepository<Product, Guid> repository,
    IValidator<CreateProduct> validator) : IRequestHandler<CreateProduct, ProductView>
{
    public async Task<ProductView> Handle(CreateProduct request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var product = Product.Create(request.Name, request.Price);
        repository.Add(product);
        await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        return new ProductView(product.Id, product.Name, product.Price);
    }
}
