using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Neo.Infrastructure.Features.Cache;

/// <summary>Compatibility namespace using the same atomic lease implementation.</summary>
public class RedisDistributedLock(IConnectionMultiplexer connection, ILogger<RedisDistributedLock> logger)
    : Outbox.RedisDistributedLock(connection, logger);

