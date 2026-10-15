using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Infrastructure.Features.Queue.Hangfire;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.Advisory;

// A generic host: no HTTP listener, public callback, dashboard or implicit seed.
var builder = Host.CreateApplicationBuilder(args);
if (!builder.Configuration.GetValue<bool>("Orchestration:SimulationEnabled") && !builder.Configuration.GetValue<bool>("Harness:Enabled") &&
    !builder.Configuration.GetValue<bool>("AgnoAdvisory:Enabled"))
{
    Console.WriteLine("Worker disabled. Explicitly enable simulation, HTTP harness or private advisory processing.");
    return;
}
var product = builder.Configuration["NEO_ORCHESTRATION_SQL"] ?? builder.Configuration.GetConnectionString("Orchestration")
    ?? throw new InvalidOperationException("Independent orchestration connection is required.");
var database = builder.Configuration["Orchestration:DatabaseName"] ?? "NeoAgentOrchestration";
OrchestrationProvisioner.ValidateDestination(product, database);
if (!string.Equals(builder.Configuration["Hangfire:Storage"], "SqlServer", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("This worker requires an independent SQL Server Hangfire catalog.");
var jobs = new SqlConnectionStringBuilder(builder.Configuration["Hangfire:ConnectionString"]
    ?? throw new InvalidOperationException("Hangfire connection is required."));
var expectedJobs = builder.Configuration["Orchestration:JobsDatabaseName"] ?? "NeoAgentOrchestration_Jobs";
OrchestrationProvisioner.ValidateDestination(jobs.ConnectionString, expectedJobs);
if (string.Equals(jobs.InitialCatalog, database, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Product and jobs catalogs must be distinct.");
var queues = builder.Configuration.GetSection("Hangfire:Queues").Get<string[]>() ?? ["default", "outbox"];
if (!queues.Contains("outbox", StringComparer.Ordinal)) throw new InvalidOperationException("Worker must consume the outbox queue.");
builder.Services.AddOrchestrationSql(product);
builder.Services.AddNeoHangfire(builder.Configuration);
builder.Services.AddSimulationRunWorker();
builder.Services.AddHttpHarness();
if (builder.Configuration.GetValue<bool>("AgnoAdvisory:Enabled"))
{
    _ = AdvisoryTransport.Endpoint(builder.Configuration);
    if (builder.Configuration["AgnoAdvisory:TokenFile"] is not { Length: > 0 } credentialPath || !File.Exists(credentialPath))
        throw new InvalidOperationException("Configure an accessible worker-only advisory credential file before startup.");
    builder.Services.AddAgnoAdvisoryWorker();
}
if (builder.Configuration.GetValue<bool>("AgentGateway:WorkerEnabled"))
{
    if (!builder.Configuration.GetValue<bool>("AgentGateway:Enabled"))
        throw new InvalidOperationException("Enable the gateway explicitly before its worker.");
    var gatewayConnection = builder.Configuration.GetConnectionString("AgentGateway")
        ?? throw new InvalidOperationException("Independent gateway connection is required.");
    var gatewayCatalog = new SqlConnectionStringBuilder(gatewayConnection).InitialCatalog;
    if (string.Equals(gatewayCatalog, jobs.InitialCatalog, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(gatewayCatalog, database, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Gateway, product and jobs catalogs must be distinct.");
    builder.Services.AddGatewayJournal(builder.Configuration, gatewayConnection).AddGatewayWorker();
}
using var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestrationDbContext>();
    if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Provision the product database before starting the worker.");
    if (builder.Configuration.GetValue<bool>("AgentGateway:WorkerEnabled"))
    {
        if (!(await GatewayProvisioner.HealthAsync(builder.Configuration.GetConnectionString("AgentGateway")!,
            builder.Configuration["AgentGateway:DatabaseName"] ?? "FanasaAgentGateway", CancellationToken.None)).Ready)
            throw new InvalidOperationException("Provision and verify the gateway database before starting the worker.");
    }
}
await host.RunAsync();
