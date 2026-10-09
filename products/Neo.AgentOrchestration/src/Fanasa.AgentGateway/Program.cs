using Microsoft.AspNetCore.HttpOverrides;
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
var gatewayConnection = builder.Configuration.GetConnectionString("AgentGateway")
    ?? throw new InvalidOperationException("Independent gateway connection is required.");
builder.Services.AddGatewayJournal(builder.Configuration, gatewayConnection);
var app = builder.Build();
if (!(await GatewayProvisioner.HealthAsync(gatewayConnection,
    builder.Configuration["AgentGateway:DatabaseName"] ?? "FanasaAgentGateway", CancellationToken.None)).Ready)
    throw new InvalidOperationException("Provision and verify the gateway database before starting ingress.");
app.UseForwardedHeaders();
app.MapGatewayEndpoints();
await app.RunAsync();

public partial class Program;
