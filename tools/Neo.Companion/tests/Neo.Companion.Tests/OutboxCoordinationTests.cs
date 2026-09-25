using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Neo.Application.Features.Outbox;
using Neo.Application.Features.Outbox.Dto;
using Neo.Application.Features.Outbox.Implementation;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Cache;
using Neo.Infrastructure.Features.Cache;
using Neo.Infrastructure.Features.Outbox;
using StackExchange.Redis;
using Xunit;
using RedisLease = Neo.Infrastructure.Features.Outbox.RedisDistributedLock;

namespace Neo.Companion.Tests;

public sealed class OutboxCoordinationTests
{
    [Fact]
    public void New_storage_keys_do_not_alias_tenant_and_operation_delimiters() =>
        Assert.NotEqual(IdempotencyKeyHasher.CreateStorageKey("a:b", "c"), IdempotencyKeyHasher.CreateStorageKey("a", "b:c"));
    [Fact]
    public async Task Losing_duplicate_is_retired_before_winner_is_returned()
    {
        var store = new Mock<IOutboxStore>(); var keys = new Mock<IIdempotencyStore<Message>>(); var scheduler = new Mock<IOutboxJobScheduler>();
        OutboxMessage? loser = null;
        store.Setup(x => x.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>())).Callback<OutboxMessage,CancellationToken>((row, _) =>
        { Assert.Equal(OutboxState.PendingIdempotency, row.OutboxState); row.Id = 2; loser = row; }).Returns(Task.CompletedTask);
        keys.SetupSequence(x => x.GetAsync("key", "tenant", It.IsAny<CancellationToken>())).ReturnsAsync((IdempotencyRecord?)null)
            .ReturnsAsync(new IdempotencyRecord { OutboxId = 1 });
        keys.Setup(x => x.AddAsync("key", "tenant", 2, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        store.Setup(x => x.GetOutboxResponseAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new OutboxResponse(1, OutboxState.Queued, "winner", "key"));
        store.Setup(x => x.FinishAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var result = await new OutboxMessageProcessor<Message>(store.Object, keys.Object, scheduler.Object).EnqueueAsync(new Message(), "key", "tenant", TestContext.Current.CancellationToken);
        Assert.NotNull(loser); Assert.Equal(OutboxState.DuplicateIdempotencyKey, loser.OutboxState);
        store.Verify(x => x.FinishAsync(loser, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, result.OutboxId); Assert.Empty(scheduler.Invocations);
    }
    [Fact]
    public async Task Cache_without_atomic_capability_is_rejected()
    {
        var store = new IdempotencyStoreWithCacheService<Message>(Mock.Of<ICacheService>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("key", "tenant", 1, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Atomic_memory_idempotency_has_one_winner_across_service_instances()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 32).Select(id => Task.Run(() =>
            new IdempotencyStoreWithCacheService<Message>(new MemoryCacheService(cache)).AddAsync("same", "tenant", id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.Single(outcomes, x => x);
    }
    [Fact]
    public async Task Expired_memory_owner_cannot_release_new_owner()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var old = new MemoryDistributedLock(cache, NullLogger<MemoryDistributedLock>.Instance);
        var current = new MemoryDistributedLock(cache, NullLogger<MemoryDistributedLock>.Instance);
        var contender = new MemoryDistributedLock(cache, NullLogger<MemoryDistributedLock>.Instance);
        Assert.True(await old.TryAcquireAsync("key", TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken));
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.True(await current.TryAcquireAsync("key", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        await old.ReleaseAsync("key", TestContext.Current.CancellationToken);
        Assert.False(await contender.TryAcquireAsync("key", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }
    public sealed class Message : IOutboxMessage;
}

public sealed class RedisCoordinationTests
{
    public static bool RedisAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEO_TEST_REDIS"));
    [Fact(Skip = "Set NEO_TEST_REDIS to an isolated test Redis; exercised in messaging CI.", SkipUnless = nameof(RedisAvailable))]
    public async Task Real_redis_enforces_exclusivity_owner_release_and_atomic_idempotency()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("NEO_TEST_REDIS")!);
        var key = "test-" + Guid.NewGuid();
        var old = new RedisLease(connection, NullLogger<RedisLease>.Instance);
        var current = new RedisLease(connection, NullLogger<RedisLease>.Instance);
        var contender = new RedisLease(connection, NullLogger<RedisLease>.Instance);
        Assert.True(await old.TryAcquireAsync(key, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        Assert.False(await current.TryAcquireAsync(key, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.True(await current.TryAcquireAsync(key, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await old.ReleaseAsync(key, TestContext.Current.CancellationToken);
        Assert.False(await contender.TryAcquireAsync(key, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await current.ReleaseAsync(key, TestContext.Current.CancellationToken);
        var store = new IdempotencyStoreRedis<OutboxCoordinationTests.Message>(connection);
        try
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(1, 32).Select(id => store.AddAsync(key, "test", id, TestContext.Current.CancellationToken)));
            Assert.Single(outcomes, x => x);
        }
        finally { await store.RemoveAsync(key, "test", CancellationToken.None); }
    }
}
