using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neo.Infrastructure.Data.Repository.Ef;
using ProductCatalog.Domain;

namespace ProductCatalog.Infrastructure;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : EfDbContext<CatalogDbContext>(options)
{
    protected override Assembly ContextAssembly => typeof(CatalogDbContext).Assembly;
    public DbSet<Product> Products => Set<Product>();
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> product)
    {
        product.HasKey(x => x.Id);
        product.Property(x => x.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
        product.Property(x => x.Price).HasPrecision(18, 2);
        product.Ignore(x => x.DomainEvents);
    }
}

public sealed class ProductCommandRepository(CatalogDbContext context)
    : EfCommandRepository<Product, Guid, CatalogDbContext>(context);

public sealed class ProductQueryRepository(CatalogDbContext context)
    : EfQueryRepository<Product, Guid, CatalogDbContext>(context);
