using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Neo.Infrastructure.Features.Outbox;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoOutboxWithRedis(this IServiceCollection services, IConfiguration configuration)
    {
        AddRedisConnection(services, configuration);
        services.AddScoped(typeof(IIdempotencyStore<>), typeof(IdempotencyStoreRedis<>));
        services.AddScoped<IDistributedLock, RedisDistributedLock>();
        return services;
    }

    private static void AddRedisConnection(IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("Redis connection string is required for distributed outbox coordination.");
        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connection));
    }
    public static IServiceCollection AddNeoOutboxWithMongo(this IServiceCollection services, IConfiguration configuration)
    {
        // Connection string & DB name from config
        var connectionString = configuration.GetConnectionString("MongoOutbox")
                               ?? throw new InvalidOperationException("Missing MongoOutbox connection string");
        var databaseName = configuration["Outbox:Mongo:DatabaseName"]
                           ?? "OutboxDb";

        // Register Mongo
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            return client.GetDatabase(databaseName);
        });

        // Register idempotency store
        services.AddScoped(typeof(IIdempotencyStore<>), typeof(IdempotencyStoreMongoDb<>));

        // Register Redis distributed lock for production
        AddRedisConnection(services, configuration);
        services.AddScoped<IDistributedLock, RedisDistributedLock>();

        return services;
    }

    public static IServiceCollection AddNeoOutboxWithCatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddScoped(typeof(IIdempotencyStore<>), typeof(IdempotencyStoreWithCacheService<>));

        // Register Memory distributed lock for development
        services.AddScoped<IDistributedLock, MemoryDistributedLock>();

        return services;
    }
}
