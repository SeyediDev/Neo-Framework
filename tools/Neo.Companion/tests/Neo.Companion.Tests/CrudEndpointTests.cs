using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CrudResourceDemo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Crud;
using Xunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Neo.Endpoint.Controller.Base;

namespace Neo.Companion.Tests;

public sealed class CrudEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(true, 405)]
    [InlineData(false, 403)]
    public async Task Disabled_or_denied_operations_never_invoke_mutation(bool disabled, int expectedStatus)
    {
        var service = new Mock<ICrudService<RestrictedDefinition,CreateProduct,UpdateProduct,ProductView,Guid>>(MockBehavior.Strict);
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(x => x.AuthorizeAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<object>(), "write"))
            .ReturnsAsync(AuthorizationResult.Failed());
        var controller = new RestrictedController(service.Object, new RestrictedDefinition(disabled), authorization.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([], "test")) } };
        var response = await controller.Create(new("denied", null), Ct);
        Assert.Equal(expectedStatus, Assert.IsType<StatusCodeResult>(response).StatusCode);
        service.VerifyNoOtherCalls();
    }

    private sealed class RestrictedDefinition(bool disabled) : CrudDefinition<CreateProduct,UpdateProduct,ProductView,Product,Guid>
    {
        public override string Name => "restricted";
        public override CrudOperation Operations => disabled ? CrudOperation.Read : CrudOperation.All;
        public override IReadOnlyDictionary<CrudOperation,string?> Policies => new Dictionary<CrudOperation,string?> { [CrudOperation.Create] = "write" };
        public override Product Create(CreateProduct input) => throw new NotImplementedException();
        public override void Update(Product entity, UpdateProduct input) => throw new NotImplementedException();
        public override ProductView Read(Product entity) => throw new NotImplementedException();
    }
    private sealed class RestrictedController(ICrudService<RestrictedDefinition,CreateProduct,UpdateProduct,ProductView,Guid> service,
        RestrictedDefinition definition, IAuthorizationService authorization)
        : GenericCrudControllerBase<CreateProduct,UpdateProduct,ProductView,Product,Guid,RestrictedDefinition>(service, definition, authorization);
    [Fact]
    public async Task HTTP_contract_enforces_auth_validation_location_and_versions()
    {
        await using var app = new Factory(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/products", new CreateProduct("first", "اول"), Ct)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/products", new CreateProduct("", null), Ct)).StatusCode);
        var response = await client.PostAsJsonAsync("/products", new CreateProduct("first", "اول"), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<CrudItem<Guid,ProductView>>(Ct))!;
        Assert.NotEqual(Guid.Empty, item.Version);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location, Ct)).StatusCode);
        var changed = await client.PutAsJsonAsync($"/products/{item.Id}", new CrudUpdate<UpdateProduct>(new("second", "دوم"), item.Version), Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var stale = await client.PutAsJsonAsync($"/products/{item.Id}", new CrudUpdate<UpdateProduct>(new("lost", null), item.Version), Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("stale_version", await stale.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/products/{item.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/products/{item.Id}?expectedVersion={item.Version}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/products/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/products?pageNumber=not-a-number", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/products?sort=CreatedAtUtc", Ct)).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        Assert.Equal("دوم", (await db.Set<ProductTranslation>().SingleAsync(Ct)).Text);
        Assert.Equal(2, await db.Set<OutboxMessage>().CountAsync(Ct));
    }

    [Fact]
    public async Task HTTP_failure_rolls_back_product_translation_and_outbox()
    {
        await using var app = new Factory(rollback: true); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/products", new CreateProduct("first", "اول"), Ct)).StatusCode);
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        Assert.Empty(await db.Set<Product>().ToListAsync(Ct));
        Assert.Empty(await db.Set<ProductTranslation>().ToListAsync(Ct));
        Assert.Empty(await db.Set<OutboxMessage>().ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Doctor_checks_metadata_and_policies_without_creating_schema(bool missingPolicy, bool pessimistic)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(Ct);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Concurrency"] = pessimistic ? "Pessimistic" : "Optimistic" }).Build());
        services.AddDbContext<CatalogContext>(o => o.UseSqlite(connection).AddInterceptors(new NeoConcurrencyInterceptor()));
        services.AddNeoCrudResource<CatalogContext,ProductDefinition,CreateProduct,UpdateProduct,ProductView,Product,Guid>();
        services.AddAuthorization(o => { if (!missingPolicy) o.AddPolicy("catalog.write", p => p.RequireAuthenticatedUser()); });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var report = await CrudRuntimeDoctor.CheckAsync(provider, checkConnectivity: true, Ct);
        Assert.Equal(!missingPolicy && !pessimistic, report.Healthy);
        if (missingPolicy) Assert.Contains(report.Findings, x => x.Code == "missing_policy");
        if (pessimistic) Assert.Contains(report.Findings, x => x.Detail.Contains("SQL Server"));
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Doctor_reports_missing_DI_without_leaking_exception_messages()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CrudResourceRegistration(typeof(ProductDefinition), typeof(CatalogContext),
            _ => throw new InvalidOperationException("private-connection-value"), _ => throw new NotImplementedException(), (_,_) => Task.FromResult(true)));
        await using var provider = services.BuildServiceProvider();
        var report = await CrudRuntimeDoctor.CheckAsync(provider, ct: Ct);
        Assert.False(report.Healthy); Assert.Equal("resolution_failed", Assert.Single(report.Findings).Code);
        Assert.DoesNotContain("private-connection-value", report.Findings[0].Detail);
    }

    private sealed class Factory : WebApplicationFactory<CrudDemoProgram>
    {
        private readonly SqliteConnection anchor = new($"Data Source=api-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        private readonly bool rollback;
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public Factory(bool rollback = false) { this.rollback = rollback; anchor.Open(); }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?>
                { ["DemoToken"] = Token, ["DemonstrateRollback"] = rollback.ToString() }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CatalogContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CatalogContext>>();
                services.AddDbContext<CatalogContext>(o => o.UseSqlite(anchor.ConnectionString).AddInterceptors(new NeoConcurrencyInterceptor()));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); await anchor.DisposeAsync(); }
    }
}
