using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Fanasa.AgentGateway;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Configuration.GetValue<bool>("AgentGateway:Enabled"))
{
    Console.WriteLine("Agent gateway disabled. No HTTP listener or execution is started.");
    return;
}
// Separate private ingress, never the public task API. Do not log dispatch
// bodies, Authorization, model credentials or upstream exception details.
builder.WebHost.UseUrls(builder.Configuration["AgentGateway:ListenUrl"] ?? "http://127.0.0.1:18112");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayEndpoints.MaxBodyBytes);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto); // framework's known loopback proxies only
builder.Services.AddGatewayJournal(builder.Configuration,
    builder.Configuration.GetConnectionString("AgentGateway") ?? throw new InvalidOperationException("Independent gateway connection is required."));
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Provision the gateway database before starting ingress.");
}
app.UseForwardedHeaders();
app.MapGatewayEndpoints();
await app.RunAsync();

public partial class Program;
