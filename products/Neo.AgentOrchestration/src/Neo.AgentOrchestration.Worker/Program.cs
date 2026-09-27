using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Infrastructure.Features.Queue.Hangfire;

// A generic host: no HTTP listener, public callback, dashboard or implicit seed.
var builder = Host.CreateApplicationBuilder(args);
if (!builder.Configuration.GetValue<bool>("Orchestration:SimulationEnabled"))
{
    Console.WriteLine("Simulation worker disabled. Set Orchestration:SimulationEnabled explicitly to run it.");
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
using var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestrationDbContext>();
    if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Provision the product database before starting the worker.");
}
await host.RunAsync();
