using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Neo.Domain.Repository;
using ProductCatalog.Application;
using ProductCatalog.Domain;
using ProductCatalog.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// Keep the teaching host portable; no Windows Event Log permissions are needed.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Catalog") ?? "Data Source=catalog.db"));
builder.Services.AddScoped<ICommandRepository<Product, Guid>, ProductCommandRepository>();
builder.Services.AddScoped<IQueryRepository<Product, Guid>, ProductQueryRepository>();
builder.Services.AddScoped<IValidator<CreateProduct>, CreateProductValidator>();
builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<CreateProduct>());
var app = builder.Build();

// EnsureCreated is intentionally limited to this small, local learning sample.
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.EnsureCreatedAsync();

app.MapPost("/products", async (CreateProduct command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var product = await sender.Send(command, ct);
        return Results.Created($"/products/{product.Id}", product);
    }
    catch (ValidationException error)
    {
        return Results.ValidationProblem(error.Errors.GroupBy(x => x.PropertyName)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray()));
    }
});

app.MapGet("/products/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var product = await sender.Send(new GetProduct(id), ct);
    return product is null ? Results.NotFound() : Results.Ok(product);
});
app.Run();

public partial class Program;
