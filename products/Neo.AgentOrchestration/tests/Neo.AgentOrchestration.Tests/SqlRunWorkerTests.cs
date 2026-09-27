using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Queue;
using Neo.Domain.Entities.Common;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Infrastructure.Features.Queue.Hangfire;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

[CollectionDefinition("LiveRunWorker", DisableParallelization = true)]
public sealed class LiveRunWorkerCollection;

[Collection("LiveRunWorker")]
public sealed class SqlRunWorkerTests
{
    [Fact]
    public async Task Neo_hangfire_scheduler_and_actual_sql_worker_complete_the_persisted_run_chain()
    {
        var ct = TestContext.Current.CancellationToken;
        // The dispatcher is installation-wide. Never point it at the catalog
        // containing other delivery tests' deliberately interrupted messages.
        var f = await Fixture.Create("NeoAgentOrchestration_WorkerVerification");
        var setup = await SqlRunTests.Configure(f); var item = await SqlRunTests.Ready(f);
        await SqlRunTests.Start(f, setup, item);
        var jobs = new SqlConnectionStringBuilder(f.Connection) { InitialCatalog = "NeoAgentOrchestration_JobsVerification" };
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(jobs.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync(ct); await using var command = master.CreateCommand();
            command.CommandText = "IF DB_ID(N'NeoAgentOrchestration_JobsVerification') IS NULL CREATE DATABASE [NeoAgentOrchestration_JobsVerification]";
            await command.ExecuteNonQueryAsync(ct);
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Hangfire:Storage"] = "SqlServer", ["Hangfire:ConnectionString"] = jobs.ConnectionString,
            ["Hangfire:WorkerCount"] = "2", ["Hangfire:Queues:0"] = "outbox" });
        builder.Services.AddOrchestrationSql(f.Connection);
        builder.Services.AddSingleton<TimeProvider>(f.Clock);
        builder.Services.AddNeoHangfire(builder.Configuration);
        builder.Services.AddSimulationRunWorker();
        using var host = builder.Build();
        await host.StartAsync(ct);
        try
        {
            // Resolve the instrumented Neo executor too, catching incomplete
            // telemetry/requester DI rather than relying on host build alone.
            using (var check = host.Services.CreateScope())
            {
                Assert.NotNull(check.ServiceProvider.GetRequiredService<IJobExecuter>());
                Assert.NotNull(check.ServiceProvider.GetRequiredService<IRecurringJobsManager>());
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var completed = false;
            while (!deadline.IsCancellationRequested)
            {
                using var scope = host.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IProcessOutboxRecurringJob>().Run();
                await using var db = f.Factory.CreateDbContext();
                if (await db.WorkItems.AnyAsync(x => x.Id == item.Id && x.Status == WorkItemStatus.Done, deadline.Token))
                { completed = true; break; }
                Assert.False(await db.Deliveries.AnyAsync(x => x.WorkItemId == item.Id && x.Outbox.OutboxState == OutboxState.Failed, deadline.Token));
                await Task.Delay(750, deadline.Token);
            }
            Assert.True(completed, "Live Hangfire worker did not finish the simulated run chain.");
            await using var read = f.Factory.CreateDbContext();
            var rows = await read.Deliveries.Include(x => x.Outbox).Where(x => x.WorkItemId == item.Id).ToArrayAsync(ct);
            Assert.Equal(6, rows.Length);
            Assert.All(rows, x => { Assert.Equal(OutboxState.Processed, x.Outbox.OutboxState); Assert.False(string.IsNullOrWhiteSpace(x.Outbox.JobId)); });
            Assert.Equal(2, await read.AgentRuns.CountAsync(x => x.WorkItemId == item.Id, ct));
            Assert.False(await read.Set<WorkItemTimeEntry>().AnyAsync(x => x.WorkItemId == item.Id && x.EndedAtUtc == null, ct));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await host.StopAsync(stop.Token);
        }
        // Test catalogs and all result records are retained; no delete/drop.
    }
}
