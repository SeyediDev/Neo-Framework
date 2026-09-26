using System.Text.Json;
using CrudResourceDemo;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.Infrastructure.Features.Crud;

namespace CrudResourceDemo;

public sealed class CrudDemoProgram
{
public static async Task Main(string[] args)
{
var doctor = args.Contains("--doctor", StringComparer.Ordinal);
var connect = args.Contains("--check-connectivity", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(x => x is not ("--doctor" or "--check-connectivity")).ToArray());
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5092");
builder.Services.AddControllers().AddApplicationPart(typeof(ProductsController).Assembly);
builder.Services.Configure<ApiBehaviorOptions>(o => o.SuppressModelStateInvalidFilter = true);
builder.Services.AddAuthentication("demo").AddScheme<AuthenticationSchemeOptions,DemoAuthentication>("demo", _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy("catalog.write", p => p.RequireAuthenticatedUser().RequireClaim("permission", "catalog.write")));
builder.Services.AddDbContext<CatalogContext>(o =>
{
    var connection = builder.Configuration.GetConnectionString("Demo");
    if (connection is null) o.UseSqlite("Data Source=neo-crud-demo.db");
    else
    {
        if (!new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("NeoCrudDemo", StringComparison.Ordinal))
            throw new InvalidOperationException("Use an isolated NeoCrudDemo database for this teaching application.");
        o.UseSqlServer(connection);
    }
    o.AddInterceptors(new NeoConcurrencyInterceptor());
});
builder.Services.AddNeoCrudResource<CatalogContext,ProductDefinition,CreateProduct,UpdateProduct,ProductView,Product,Guid>();
builder.Services.AddScoped<ICrudTransactionParticipant<CatalogContext,Product>,ProductChanges>();
var app = builder.Build();
var report = await CrudRuntimeDoctor.CheckAsync(app.Services, connect);
if (doctor)
{
    Console.WriteLine(JsonSerializer.Serialize(report));
    Environment.ExitCode = report.Healthy ? 0 : 1;
    await app.DisposeAsync();
    return;
}
if (!report.Healthy) throw new InvalidOperationException(JsonSerializer.Serialize(report));
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CatalogContext>().Database.EnsureCreatedAsync();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
await app.RunAsync();

}
}
