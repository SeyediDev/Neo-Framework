using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Neo.Domain.Entities.Base;
using Neo.Infrastructure.Data.Interceptors;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class DomainEventTests
{
    [Fact]
    public void Bulk_add_through_interface_records_the_events()
    {
        IDomainEventEntity entity = new Aggregate();
        var first = new Changed(); var second = new Changed();
        entity.AddDomainEvents([first, second]);
        Assert.Equal(new BaseEvent[] { first, second }, entity.DomainEvents);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cascading_events_are_drained_and_removed_only_after_success(bool synchronous)
    {
        await using var f = await Fixture.Create();
        var first = new Changed(); var second = new Changed();
        f.Entity.AddDomainEvent(first);
        f.Mediator.Setup(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()))
            .Callback<INotification, CancellationToken>((message, _) =>
            {
                Assert.Contains(first, f.Entity.DomainEvents);
                if (ReferenceEquals(message, first)) f.Entity.AddDomainEvent(second);
            }).Returns(Task.CompletedTask);
        if (synchronous) f.Db.SaveChanges(); else await f.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(f.Entity.DomainEvents);
        f.Mediator.Verify(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Fact]
    public async Task Handler_failure_keeps_failed_and_pending_events_and_propagates_original_error()
    {
        await using var f = await Fixture.Create();
        var first = new Changed(); var second = new Changed(); var error = new InvalidOperationException("handler failed");
        f.Entity.AddDomainEvent(first); f.Entity.AddDomainEvent(second);
        f.Mediator.Setup(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>())).ThrowsAsync(error);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(new BaseEvent[] { first, second }, f.Entity.DomainEvents);
        f.Mediator.Verify(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task Cancellation_reaches_handler_and_preserves_pending_events()
    {
        await using var f = await Fixture.Create();
        using var source = new CancellationTokenSource();
        f.Entity.AddDomainEvent(new Changed());
        f.Mediator.Setup(x => x.Publish(It.IsAny<INotification>(), source.Token))
            .Returns<INotification,CancellationToken>((_, token) => { source.Cancel(); return Task.FromCanceled(token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Db.SaveChangesAsync(source.Token));
        Assert.Single(f.Entity.DomainEvents);
    }
    [Fact]
    public async Task Database_failure_does_not_discard_domain_events()
    {
        await using var f = await Fixture.Create();
        f.Entity.AddDomainEvent(new Changed()); f.Entity.Name = null!;
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Single(f.Entity.DomainEvents);
        Assert.Equal(0, await f.Db.Set<Aggregate>().CountAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Recursive_save_is_rejected_instead_of_partially_persisting_work()
    {
        await using var f = await Fixture.Create();
        f.Entity.AddDomainEvent(new Changed());
        f.Mediator.Setup(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()))
            .Returns<INotification,CancellationToken>(async (_, token) => { await f.Db.SaveChangesAsync(token); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("recursively", error.Message); Assert.Single(f.Entity.DomainEvents);
    }
    public sealed class Changed : BaseEvent;
    public sealed class Aggregate : BaseEntity<Guid> { public string Name { get; set; } = "order"; }
    private sealed class Context(DbContextOptions<Context> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<Aggregate>().Property(x => x.Name).IsRequired();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public Mock<IMediator> Mediator { get; } = new();
        public Context Db { get; private set; } = null!;
        public Aggregate Entity { get; } = new() { Id = Guid.NewGuid() };
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.connection.OpenAsync(TestContext.Current.CancellationToken);
            fixture.Mediator.Setup(x => x.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            fixture.Db = new Context(new DbContextOptionsBuilder<Context>().UseSqlite(fixture.connection)
                .AddInterceptors(new DispatchDomainEventsInterceptor(fixture.Mediator.Object)).Options);
            await fixture.Db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            fixture.Db.Add(fixture.Entity); return fixture;
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
