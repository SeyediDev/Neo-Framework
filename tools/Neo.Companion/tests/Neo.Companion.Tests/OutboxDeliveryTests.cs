using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Application.Features.Queue;
using Neo.Domain.Entities.Common;
using Neo.Infrastructure.Features.Outbox;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class OutboxDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Dispatch_claim_has_one_owner_and_recovers_expired_lease()
    {
        await using var f = await Fixture.Create();
        var token = Guid.NewGuid();
        Assert.NotNull(await f.Store.ClaimDispatchAsync(1, token, TimeSpan.FromMinutes(5), Ct));
        await using var other = f.NewContext();
        var second = new EfOutboxStore<Context>(other);
        Assert.Null(await second.ClaimDispatchAsync(1, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
        await f.ExpireLease();
        var replacement = Guid.NewGuid();
        Assert.NotNull(await second.ClaimDispatchAsync(1, replacement, TimeSpan.FromMinutes(5), Ct));
        await f.Store.CompleteDispatchAsync(1, token, "stale", null, Ct);
        Assert.Equal(replacement, (await f.Store.GetAsync(1, Ct))!.DeliveryLeaseId);
        await second.CompleteDispatchAsync(1, replacement, "live", null, Ct);
        Assert.Equal("live", (await f.Store.GetAsync(1, Ct))!.JobId);
    }

    [Fact]
    public async Task Fast_execution_commits_effect_and_dispatcher_cannot_overwrite_processed()
    {
        await using var f = await Fixture.Create();
        var dispatch = Guid.NewGuid(); var execution = Guid.NewGuid();
        await f.Store.ClaimDispatchAsync(1, dispatch, TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(await f.Store.ClaimExecutionAsync(1, execution, TimeSpan.FromMinutes(5), Ct));
        await f.Store.ExecuteClaimedAsync(1, execution, (_, _) => { f.Db.Add(new Effect { Id = 1 }); return Task.CompletedTask; }, Ct);
        await f.Store.CompleteDispatchAsync(1, dispatch, "late-job-id", null, Ct);
        Assert.Equal(OutboxState.Processed, (await f.Store.GetAsync(1, Ct))!.OutboxState);
        Assert.Equal(1, await f.Db.Set<Effect>().CountAsync(Ct));
        Assert.Null(await f.Store.ClaimExecutionAsync(1, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task Handler_failure_rolls_back_even_saved_effect_and_preserves_original_exception()
    {
        await using var f = await Fixture.Create();
        var token = await f.ReadyForExecution(); var error = new InvalidOperationException("payment unavailable");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ExecuteClaimedAsync(1, token, async (_, ct) =>
        {
            f.Db.Add(new Effect { Id = 1 }); await f.Db.SaveChangesAsync(ct); throw error;
        }, Ct)));
        Assert.Equal(0, await f.Db.Set<Effect>().CountAsync(Ct));
        var failed = (await f.Store.GetAsync(1, Ct))!;
        Assert.Equal(OutboxState.ExecutionRetrying, failed.OutboxState);
        Assert.Equal(error.Message, failed.ProcessError);
        Assert.Empty(await f.Store.GetRequested(15, Ct));
        await f.Db.Set<OutboxMessage>().ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)), Ct);
        Assert.Single(await f.Store.GetRequested(15, Ct));
    }

    [Theory]
    [InlineData(OutboxState.Processing)]
    [InlineData(OutboxState.Dispatching)]
    public async Task Crash_on_final_attempt_becomes_terminal(OutboxState state)
    {
        await using var f = await Fixture.Create();
        await f.Db.Set<OutboxMessage>().ExecuteUpdateAsync(s => s.SetProperty(x => x.OutboxState, state)
            .SetProperty(x => x.ProcessTryCount, 3).SetProperty(x => x.PublishTryCount, 3)
            .SetProperty(x => x.DeliveryLeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)), Ct);
        Assert.Empty(await f.Store.GetRequested(15, Ct));
        Assert.Equal(OutboxState.Failed, (await f.Store.GetAsync(1, Ct))!.OutboxState);
    }

    [Fact]
    public async Task Stale_execution_token_cannot_commit_business_effect()
    {
        await using var f = await Fixture.Create();
        var old = await f.ReadyForExecution(); await f.ExpireLease();
        var owner = Guid.NewGuid(); await f.Store.ClaimExecutionAsync(1, owner, TimeSpan.FromMinutes(5), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ExecuteClaimedAsync(1, old,
            (_, _) => { f.Db.Add(new Effect { Id = 1 }); return Task.CompletedTask; }, Ct));
        Assert.Equal(0, await f.Db.Set<Effect>().CountAsync(Ct));
        Assert.Equal(owner, (await f.Store.GetAsync(1, Ct))!.DeliveryLeaseId);
    }

    [Fact]
    public async Task Hangfire_job_carries_outbox_id_instead_of_serialized_payload()
    {
        var client = new Mock<IBackgroundJobClient>(); Job? captured = null;
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job,IState>((job, _) => captured = job).Returns("job");
        var executor = (IJobExecuter)Activator.CreateInstance(typeof(EfOutboxRegistration).Assembly.GetType("Neo.Infrastructure.Features.Queue.Hangfire.HangfireJobExecuter", true)!, client.Object)!;
        await new DefaultOutboxJobScheduler(executor).ScheduleOutboxMessageAsync(new OutboxMessage { Id = 42, DeliveryLeaseId = Guid.NewGuid() }, Ct);
        Assert.NotNull(captured); Assert.Equal(typeof(IOutboxExecutionJob), captured.Type);
        Assert.Equal(42L, captured.Args[0]); Assert.Equal(CancellationToken.None, captured.Args[1]);
    }

    public sealed class Effect { public int Id { get; set; } }
    public sealed class Context(DbContextOptions<Context> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<OutboxMessage>().Ignore(x => x.CreatedById).Ignore(x => x.LastModifiedById);
            model.Entity<Effect>();
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public Context Db { get; private set; } = null!;
        public EfOutboxStore<Context> Store => new(Db);
        public Context NewContext() => new(new DbContextOptionsBuilder<Context>().UseSqlite(connection).Options);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.connection.OpenAsync(Ct); f.Db = f.NewContext();
            await f.Db.Database.EnsureCreatedAsync(Ct);
            f.Db.Add(new OutboxMessage { Id = 1, MessageName = "test", MessageType = "test", MessageContent = "{}" });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear(); return f;
        }
        public async Task<Guid> ReadyForExecution()
        {
            await Store.ClaimDispatchAsync(1, Guid.NewGuid(), TimeSpan.FromMinutes(5), Ct);
            var token = Guid.NewGuid(); await Store.ClaimExecutionAsync(1, token, TimeSpan.FromMinutes(5), Ct); return token;
        }
        public Task ExpireLease() => Db.Set<OutboxMessage>().ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryLeaseUntilUtc, DateTime.UtcNow.AddMinutes(-1)), Ct);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
