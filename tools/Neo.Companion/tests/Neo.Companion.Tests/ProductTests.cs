using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using ProductCatalog.Application;
using ProductCatalog.Domain;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class ProductTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Domain_rejects_missing_name(string? name) =>
        Assert.Throws<ArgumentException>(() => Product.Create(name, 10));

    [Fact]
    public void Domain_rejects_negative_price() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create("Product", -1));

    [Fact]
    public void Domain_rejects_long_name() =>
        Assert.Throws<ArgumentException>(() => Product.Create(new string('a', 201), 1));

    [Fact]
    public async Task Create_then_get_survives_application_restart()
    {
        var database = Path.Combine(Path.GetTempPath(), $"neo-catalog-{Guid.NewGuid():N}.db");
        try
        {
            ProductView? created;
            using (var app = new CatalogFactory(database))
            using (var client = app.CreateClient())
            {
                var response = await client.PostAsJsonAsync("/products", new CreateProduct("  محصول آزمایشی  ", 125.50m));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                created = await response.Content.ReadFromJsonAsync<ProductView>();
                Assert.NotNull(created);
                Assert.NotEqual(Guid.Empty, created.Id);
                Assert.Equal("محصول آزمایشی", created.Name);
                Assert.Equal($"/products/{created.Id}", response.Headers.Location?.ToString());
            }
            using (var app = new CatalogFactory(database))
            using (var client = app.CreateClient())
            {
                var saved = await client.GetFromJsonAsync<ProductView>($"/products/{created.Id}");
                Assert.Equal(created, saved);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/products/{Guid.NewGuid()}")).StatusCode);
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(database); }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("  ", 1)]
    [InlineData("Product", -1)]
    public async Task Api_rejects_invalid_product_without_persisting(string? name, int price)
    {
        var database = Path.Combine(Path.GetTempPath(), $"neo-catalog-{Guid.NewGuid():N}.db");
        try
        {
            using var app = new CatalogFactory(database);
            using var client = app.CreateClient();
            var response = await client.PostAsJsonAsync("/products", new CreateProduct(name, price));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Products";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(database); }
    }

    private sealed class CatalogFactory(string database) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Testing").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Catalog"] = $"Data Source={database}" }));
    }
}
