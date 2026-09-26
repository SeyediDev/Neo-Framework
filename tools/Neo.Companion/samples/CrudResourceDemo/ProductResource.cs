using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Base;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Concurrency;
using Neo.Endpoint.Controller.Base;
using Neo.Infrastructure.Features.Crud;

namespace CrudResourceDemo;

public sealed class Product : IEntity<Guid>, IConcurrencyVersion
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
    public string Name { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
    public static Product Create(string name)
    {
        var product = new Product { Id = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow };
        product.Rename(name); return product;
    }
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new Neo.Application.Exceptions.BadRequestException("Name must contain 1..120 characters.");
        Name = name.Trim();
    }
}
public sealed class CreateProduct(string name, string? persianName)
{
    [Required, StringLength(120)] public string Name { get; set; } = name;
    [StringLength(120)] public string? PersianName { get; set; } = persianName;
}
public sealed class UpdateProduct(string name, string? persianName)
{
    [Required, StringLength(120)] public string Name { get; set; } = name;
    [StringLength(120)] public string? PersianName { get; set; } = persianName;
}
public sealed record ProductView(string Name, DateTime CreatedAtUtc);

public sealed class ProductDefinition(IConfiguration configuration) : CrudDefinition<CreateProduct,UpdateProduct,ProductView,Product,Guid>
{
    public override string Name => "products";
    public override EntityConcurrencyMode Concurrency =>
        Enum.Parse<EntityConcurrencyMode>(configuration["Concurrency"] ?? "Optimistic", ignoreCase: true);
    public override IReadOnlyDictionary<CrudOperation,string?> Policies { get; } = new Dictionary<CrudOperation,string?>
    {
        [CrudOperation.List] = null, [CrudOperation.Read] = null,
        [CrudOperation.Create] = "catalog.write", [CrudOperation.Update] = "catalog.write", [CrudOperation.Delete] = "catalog.write"
    };
    public override IReadOnlyDictionary<string,Func<string,Expression<Func<Product,bool>>>> Filters { get; } =
        new Dictionary<string,Func<string,Expression<Func<Product,bool>>>> { ["name"] = value => row => row.Name.Contains(value) };
    public override IReadOnlyDictionary<string,Func<IQueryable<Product>,bool,IOrderedQueryable<Product>>> Sorts { get; } =
        new Dictionary<string,Func<IQueryable<Product>,bool,IOrderedQueryable<Product>>>
        { ["name"] = (query, descending) => descending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name) };
    public override Product Create(CreateProduct input) => Product.Create(input.Name);
    public override void Update(Product entity, UpdateProduct input) => entity.Rename(input.Name);
    public override ProductView Read(Product entity) => new(entity.Name, DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc));
}

[Route("products")]
public sealed class ProductsController(ICrudService<ProductDefinition,CreateProduct,UpdateProduct,ProductView,Guid> service,
    ProductDefinition definition, IAuthorizationService authorization)
    : GenericCrudControllerBase<CreateProduct,UpdateProduct,ProductView,Product,Guid,ProductDefinition>(service, definition, authorization);

public sealed class ProductTranslation
{
    public Guid ProductId { get; set; }
    public string Culture { get; set; } = "fa";
    public string Text { get; set; } = "";
}
public sealed class CatalogContext(DbContextOptions<CatalogContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>().Property(x => x.Name).HasMaxLength(120);
        model.Entity<ProductTranslation>().HasKey(x => new { x.ProductId, x.Culture });
        model.Entity<ProductTranslation>().HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<OutboxMessage>().Ignore(x => x.CreatedById).Ignore(x => x.LastModifiedById);
        model.ConfigureNeoConcurrency();
    }
}

// This sample stages an Outbox row; see HangfireOutboxDemo for dispatch and a matching command handler.
public sealed class ProductChanges(IConfiguration configuration) : ICrudTransactionParticipant<CatalogContext,Product>
{
    public async Task StageAsync(CatalogContext context, CrudOperation operation, Product entity, object? input, CancellationToken ct)
    {
        var text = input switch { CreateProduct c => c.PersianName, UpdateProduct u => u.PersianName, _ => null };
        if (operation != CrudOperation.Delete)
        {
            var translation = await context.Set<ProductTranslation>().FindAsync([entity.Id, "fa"], ct);
            if (text is null) { if (translation is not null) context.Remove(translation); }
            else if (translation is not null) translation.Text = text;
            else context.Add(new ProductTranslation { ProductId = entity.Id, Text = text });
        }
        context.Add(new OutboxMessage { MessageName = "ProductChanged", MessageType = "CrudResourceDemo.ProductChanged",
            MessageContent = JsonSerializer.Serialize(new { entity.Id, entity.Version, operation = operation.ToString(), entity.Name }) });
        if (configuration.GetValue<bool>("DemonstrateRollback"))
        {
            await context.SaveChangesAsync(ct);
            throw new Neo.Application.Exceptions.BadRequestException("Demonstrated failure after staging: entity, translation and Outbox were rolled back.");
        }
    }
}
