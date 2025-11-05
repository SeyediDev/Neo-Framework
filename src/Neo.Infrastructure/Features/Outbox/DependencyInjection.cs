using Neo.Application.Features.Outbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Neo.Infrastructure.Features.Outbox;

public static class DependencyInjection
{
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
        services.AddScoped<IDistributedLock, RedisDistributedLock>();

        return services;
    }

    public static IServiceCollection AddNeoOutboxWithCatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped(typeof(IIdempotencyStore<>), typeof(IdempotencyStoreWithCacheService<>));

        // Register Memory distributed lock for development
        services.AddScoped<IDistributedLock, MemoryDistributedLock>();

        return services;
    }
}
