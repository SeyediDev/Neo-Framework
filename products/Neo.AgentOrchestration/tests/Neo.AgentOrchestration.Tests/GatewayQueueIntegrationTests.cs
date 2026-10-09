using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Application.Features.Outbox;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Queue.Hangfire;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class SqlGatewayJournalTests
{
    [Fact]
    public async Task Existing_Hangfire_worker_picks_gateway_outbox_without_second_scheduler_or_store_replacement()
    {
        var f = await Fixture.Create();
        var product = await SqlPersistenceTests.Fixture.Create("NeoAgentOrchestration_WorkerVerification");
        var gatewayConnection = Environment.GetEnvironmentVariable("FANASA_GATEWAY_TEST_SQL")!;
        var jobs = new SqlConnectionStringBuilder(gatewayConnection) { InitialCatalog = "FanasaAgentGateway_JobsVerification" };
        await using (var sql = new SqlConnection(new SqlConnectionStringBuilder(jobs.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await sql.OpenAsync(Ct); await using var command = sql.CreateCommand();
            command.CommandText = "IF DB_ID(N'FanasaAgentGateway_JobsVerification') IS NULL CREATE DATABASE [FanasaAgentGateway_JobsVerification]";
            await command.ExecuteNonQueryAsync(Ct);
        }
        f.Adapter.State = ExternalAgentState.Completed; f.Sandbox.Exited = true;
        await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Hangfire:Storage"] = "SqlServer", ["Hangfire:ConnectionString"] = jobs.ConnectionString,
            ["Hangfire:WorkerCount"] = "1", ["Hangfire:Queues:0"] = "outbox",
            ["AgentGateway:DatabaseName"] = "FanasaAgentGateway_Verification", ["Orchestration:SimulationEnabled"] = "true"
        });
        builder.Services.AddOrchestrationSql(product.Connection);
        builder.Services.AddNeoHangfire(builder.Configuration); builder.Services.AddSimulationRunWorker();
        var schedulersBefore = builder.Services.Count(x => x.ServiceType == typeof(IHostedService));
        var productStore = builder.Services.Last(x => x.ServiceType == typeof(IOutboxStore));
        builder.Services.AddGatewayJournal(builder.Configuration, gatewayConnection).AddGatewayWorker();
        Assert.Equal(schedulersBefore, builder.Services.Count(x => x.ServiceType == typeof(IHostedService)));
        Assert.Same(productStore, builder.Services.Last(x => x.ServiceType == typeof(IOutboxStore)));
        // Test-only stand-ins: no installed sandbox/model/repository is invoked.
        builder.Services.AddSingleton<IGatewayBindings>(f.Bindings);
        builder.Services.AddSingleton<IGatewaySandbox>(f.Sandbox); builder.Services.AddSingleton<IGatewayResultDelivery>(f.Delivery);
        builder.Services.AddSingleton<TimeProvider>(f.Clock);
        using var host = builder.Build(); await host.StartAsync(Ct);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct); deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var delivered = false;
            while (!deadline.IsCancellationRequested)
            {
                await using (var scope = host.Services.CreateAsyncScope())
                    await scope.ServiceProvider.GetRequiredService<IProcessOutboxRecurringJob>().Run();
                await using var read = f.Factory.CreateDbContext();
                if ((await read.Runs.SingleAsync(deadline.Token)).Phase == GatewayPhase.Delivered &&
                    !await read.OutboxMessages.AnyAsync(x => x.OutboxState != OutboxState.Processed, deadline.Token))
                { delivered = true; break; }
                Assert.False(await read.OutboxMessages.AnyAsync(x => x.OutboxState == OutboxState.Failed, deadline.Token));
                await Task.Delay(500, deadline.Token);
            }
            Assert.True(delivered); Assert.Equal(1, f.Adapter.Submitted); Assert.Single(f.Delivery.Bodies);
            await using var final = f.Factory.CreateDbContext();
            var activations = await final.Activations.Include(x => x.Outbox).ToArrayAsync(Ct);
            Assert.Equal(3, activations.Length);
            Assert.All(activations, a => { Assert.Equal(OutboxState.Processed, a.Outbox.OutboxState); Assert.NotNull(a.Outbox.JobId); });
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await host.StopAsync(stop.Token);
        }
    }
}
